using System;
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

        /// <summary>The path that loaded, or null.</summary>
        public static string LoadedFrom { get; private set; }

        /// <summary>
        /// Best-effort: a failure here is not fatal, because on some platforms the
        /// ordinary P/Invoke resolution works on its own.
        /// </summary>
        public static void Preload()
        {
            foreach (string candidate in Candidates)
            {
                try
                {
                    dlerror();
                    IntPtr handle = dlopen(candidate, RtldNow | RtldGlobal);
                    if (handle != IntPtr.Zero)
                    {
                        LoadedFrom = candidate;
                        DiagLog.Add("engine preloaded from " + candidate);
                        return;
                    }

                    string error = Marshal.PtrToStringAnsi(dlerror()) ?? "(no dlerror)";
                    DiagLog.Add("preload failed " + candidate + ": " + error);
                }
                catch (Exception ex)
                {
                    DiagLog.Add("preload threw for " + candidate + ": " + ex.GetType().Name);
                }
            }

            DiagLog.Add("engine not preloaded; relying on P/Invoke resolution");
        }
    }
}
