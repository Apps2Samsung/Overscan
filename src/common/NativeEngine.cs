using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Overscan
{
    /// <summary>
    /// Loads chromium-efl by absolute path before anything P/Invokes it by name.
    ///
    /// On retail Tizen 5.0 the engine is at /usr/lib/libchromium-ewk.so, but
    /// Chromium.Initialize() went straight to DllNotFoundException — the loader
    /// mangles the name ("liblibchromium-ewk.so.so") and its probe path does not
    /// find it. dlopen with an absolute path and RTLD_GLOBAL sidesteps that
    /// entirely, and because the loader matches already-loaded sonames first, the
    /// later P/Invoke binds to this handle.
    ///
    /// Verified on a Samsung UE55RU7020: with a partner certificate and the DRM
    /// privileges declared, this preload succeeds and Chromium.Initialize() then
    /// returns refcount=1. Without them dlopen fails with
    /// "libmarlin.so.0: … Operation not permitted", since the engine links Marlin
    /// DRM and an author-signed app may not open it.
    /// </summary>
    internal static class NativeEngine
    {
        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        private static readonly string[] Candidates =
        {
            "/usr/lib/libchromium-ewk.so",
            "/lib/libchromium-ewk.so",
            "/usr/lib/libchromium-ewk.so.0",
        };

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        /// <summary>The engine's own entry point, called only to retry a failed init.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetArgumentsDelegate(int argc, IntPtr argv);

        private static IntPtr _handle;

        /// <summary>The path that loaded, or null.</summary>
        public static string LoadedFrom { get; private set; }

        /// <summary>
        /// Best-effort: a failure here is not fatal, because on some platforms the
        /// ordinary P/Invoke resolution works on its own.
        ///
        /// Call this **before the first elm window exists**. libchromium-ewk.so has
        /// a library constructor, <c>_ewk_force_acceleration()</c>, whose whole job
        /// is <c>setenv("ELM_ACCEL", "hw", 1)</c>, and its comment in the engine
        /// source is explicit that it has to happen "before creating elm_window"
        /// because the port has no software path. Loading the engine only once the
        /// UI was already built put that constructor on the wrong side of the
        /// window — see <c>Program.Main</c>, which is where the call now lives.
        /// </summary>
        public static void Preload()
        {
            if (_handle != IntPtr.Zero)
            {
                return;
            }

            string blocked = null;

            foreach (string candidate in Candidates)
            {
                try
                {
                    dlerror();
                    IntPtr handle = dlopen(candidate, RtldNow | RtldGlobal);
                    if (handle != IntPtr.Zero)
                    {
                        _handle = handle;
                        LoadedFrom = candidate;
                        DiagLog.Add("engine preloaded from " + candidate);
                        return;
                    }

                    string error = Marshal.PtrToStringAnsi(dlerror()) ?? "(no dlerror)";
                    DiagLog.Add("preload failed " + candidate + ": " + error);
                    blocked = blocked ?? SmackWall.BlockedSoname(error);
                }
                catch (Exception ex)
                {
                    DiagLog.Add("preload threw for " + candidate + ": " + ex.GetType().Name);
                }
            }

            DiagLog.Add("engine not preloaded; relying on P/Invoke resolution");

            // The Marlin wall, if this is still it: say which of the three refusals
            // it is rather than leaving "Operation not permitted" to be read as a
            // privilege every time. See SmackWall.
            // Off the calling thread: this runs in Main, before Elementary, so a
            // probe that does not survive the firmware would take the app down
            // before it could put anything on screen. Which is precisely what the
            // first version of it did — see SmackWall.
            SmackWall.InvestigateInBackground(blocked);
        }

        /// <summary>
        /// Hands chromium-efl the process's command line, which
        /// <c>Chromium.Initialize()</c> does not do.
        ///
        /// Only called after <c>ewk_init()</c> has already reported failure. The
        /// engine's browser-process start-up reads argv, and for a .NET app argv[0]
        /// is the shared <c>dotnet-launcher</c> rather than an app binary; the ewk
        /// samples all call <c>ewk_set_arguments</c> first, so a set where init
        /// refuses is worth one retry with the arguments supplied. TizenFX exposes
        /// no binding for it, hence dlsym on the handle we already hold.
        ///
        /// Returns what happened, for the report. Never throws.
        /// </summary>
        public static string SetArguments()
        {
            if (_handle == IntPtr.Zero)
            {
                return "no engine handle to set arguments on";
            }

            IntPtr argv = IntPtr.Zero;
            var strings = new List<IntPtr>();
            try
            {
                IntPtr symbol = dlsym(_handle, "ewk_set_arguments");
                if (symbol == IntPtr.Zero)
                {
                    return "ewk_set_arguments not exported";
                }

                // argv[0] is the only entry the engine actually reads, and passing
                // our own arguments through risks handing chromium a switch it
                // would act on, so the vector is deliberately just the executable.
                strings.Add(Marshal.StringToHGlobalAnsi("Overscan"));
                strings.Add(IntPtr.Zero);

                argv = Marshal.AllocHGlobal(IntPtr.Size * strings.Count);
                for (int i = 0; i < strings.Count; i++)
                {
                    Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
                }

                var call = (SetArgumentsDelegate)Marshal.GetDelegateForFunctionPointer(
                    symbol, typeof(SetArgumentsDelegate));
                call(1, argv);
                return "ewk_set_arguments called";
            }
            catch (Exception ex)
            {
                return "ewk_set_arguments failed — " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                // The engine copies what it needs during init; freeing after the
                // retry would mean tracking the allocation across a call that can
                // abort the process, and this runs at most once per launch.
                if (argv == IntPtr.Zero)
                {
                    foreach (IntPtr text in strings)
                    {
                        if (text != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(text);
                        }
                    }
                }
            }
        }
    }
}
