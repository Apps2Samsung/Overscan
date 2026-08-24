using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// Names the EFL subsystem that stops <c>ewk_init()</c>.
    ///
    /// <c>ewk_init()</c> returns the engine's reference count, and the *only* nine
    /// places it can return 0 are the EFL library inits it runs first (chromium-efl,
    /// <c>tizen_src/ewk/efl_integration/public/ewk_main.cc</c>):
    ///
    ///     eina_init, eina_log_domain_register, evas_init, ecore_init,
    ///     ecore_evas_init, ecore_imf_init, ecore_wl2_init (or ecore_wl_init /
    ///     ecore_x_init, depending on how the engine was built), edje_init,
    ///     eldbus_init
    ///
    /// Nothing chromium-specific runs before that return — <c>_ewk_init_web_engine()</c>
    /// is an empty function — so "ewk_init returned 0" always means one of those
    /// nine said no. Issue #17 (Q80, Tizen 5.5) is a report of exactly that, with
    /// no way to tell which one, and no way to tell from the retry either: the
    /// retry's <c>ewk_set_arguments</c> only feeds <c>CommandLineEfl::Init</c>,
    /// which <c>ewk_init</c> never reaches.
    ///
    /// So we call them ourselves, in the engine's own order, and report the first
    /// that returns 0.
    ///
    /// This is safe, and it cannot mask the fault. Every EFL init is
    /// reference-counted: one that is already up (elm_init brought most of these
    /// up long before us) returns its incremented count, and one whose underlying
    /// init genuinely fails returns 0 for us for the same reason it will return 0
    /// for the engine a moment later.
    ///
    /// The matching shutdowns are deliberately *not* called. An extra reference on
    /// eina/evas/ecore is inert — the browser never calls <c>Chromium.Shutdown()</c>
    /// either (TizenFX issue 3274) — while a shutdown ladder is one more thing that
    /// could take the process down before the report is readable, which is the
    /// failure mode this whole file exists to end.
    ///
    /// Resolution goes through dlopen/dlsym rather than <c>DllImport</c> by name
    /// for the reason <see cref="NativeEngine"/> documents: on these TVs the .NET
    /// loader mangles the soname and finds nothing.
    /// </summary>
    internal static class EflSubsystems
    {
        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitStringDelegate(string argument);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RegisterDomainDelegate(string name, string color);

        /// <summary>How a subsystem's init is reached and called.</summary>
        private enum Shape
        {
            /// <summary>int f(void)</summary>
            Plain,

            /// <summary>int f(const char*) — ecore_wl_init and ecore_x_init.</summary>
            OneString,

            /// <summary>int eina_log_domain_register(const char*, const char*)</summary>
            Domain,
        }

        private sealed class Subsystem
        {
            public Subsystem(string symbol, Shape shape, params string[] libraries)
            {
                Symbol = symbol;
                Shape = shape;
                Libraries = libraries;
            }

            public string Symbol { get; private set; }

            public Shape Shape { get; private set; }

            /// <summary>Candidate sonames, most likely first.</summary>
            public string[] Libraries { get; private set; }
        }

        /// <summary>
        /// The first six and the last two are unconditional. The display server
        /// init in between is whichever of the three the platform actually has:
        /// chromium-efl picks one at compile time (<c>USE_WAYLAND</c> plus a Tizen
        /// version test) and we cannot see which, so the first one present wins and
        /// the report says which was found.
        /// </summary>
        private static readonly Subsystem[] Order =
        {
            new Subsystem("eina_init", Shape.Plain, "libeina.so.1", "libeina.so"),
            new Subsystem("eina_log_domain_register", Shape.Domain, "libeina.so.1", "libeina.so"),
            new Subsystem("evas_init", Shape.Plain, "libevas.so.1", "libevas.so"),
            new Subsystem("ecore_init", Shape.Plain, "libecore.so.1", "libecore.so"),
            new Subsystem("ecore_evas_init", Shape.Plain, "libecore_evas.so.1", "libecore_evas.so"),
            new Subsystem("ecore_imf_init", Shape.Plain, "libecore_imf.so.1", "libecore_imf.so"),
            new Subsystem("edje_init", Shape.Plain, "libedje.so.1", "libedje.so"),
            new Subsystem("eldbus_init", Shape.Plain, "libeldbus.so.1", "libeldbus.so"),
        };

        /// <summary>Tried in order; the first that resolves is the one the engine uses.</summary>
        private static readonly Subsystem[] Display =
        {
            new Subsystem("ecore_wl2_init", Shape.Plain, "libecore_wl2.so.1", "libecore_wl2.so"),
            new Subsystem("ecore_wl_init", Shape.OneString, "libecore_wl.so.1", "libecore_wl.so"),
            new Subsystem("ecore_x_init", Shape.OneString, "libecore_x.so.1", "libecore_x.so"),
        };

        private static readonly List<string> Lines = new List<string>();

        /// <summary>One line per subsystem, for the breadcrumb trail and the probe.</summary>
        public static IList<string> Detail
        {
            get { return Lines; }
        }

        /// <summary>A single line fit for the diagnostics header.</summary>
        public static string Summary { get; private set; } = "(not checked)";

        /// <summary>
        /// The first subsystem that returned 0, or null when they all came up —
        /// in which case <c>ewk_init</c> failing is something this file does not
        /// yet model, and the captured stderr is the thing to read.
        /// </summary>
        public static string Culprit { get; private set; }

        private static bool _checked;

        /// <summary>
        /// Walks the ladder once. Never throws: a diagnostic that can take the
        /// process down is worse than no diagnostic.
        /// </summary>
        public static void Check()
        {
            if (_checked)
            {
                return;
            }

            _checked = true;

            try
            {
                foreach (Subsystem subsystem in Order)
                {
                    // The display server sits between ecore_imf and edje in
                    // ewk_init's ladder, so it is walked at that point too.
                    if (subsystem.Symbol == "edje_init")
                    {
                        CheckDisplay();
                    }

                    int result;
                    Run(subsystem, out result);
                }
            }
            catch (Exception ex)
            {
                Lines.Add("bisect threw " + ex.GetType().Name + ": " + ex.Message);
            }

            Summary = Culprit == null
                ? "all up (" + Lines.Count + " checked)"
                : "FAILED at " + Culprit;
        }

        private static void CheckDisplay()
        {
            foreach (Subsystem subsystem in Display)
            {
                if (Resolve(subsystem) != IntPtr.Zero)
                {
                    int result;
                    Run(subsystem, out result);
                    return;
                }
            }

            Lines.Add(Pad("display backend") + ": none of ecore_wl2/ecore_wl/ecore_x present");
        }

        /// <summary>
        /// Calls one init. Returns false when the symbol could not be resolved at
        /// all, which is a different fact from "it returned 0" and is reported as
        /// such — a missing libecore_wl2 means the firmware is an X11 build, not
        /// that anything is broken.
        /// </summary>
        private static bool Run(Subsystem subsystem, out int result)
        {
            result = 0;
            IntPtr symbol = Resolve(subsystem);
            if (symbol == IntPtr.Zero)
            {
                Lines.Add(Pad(subsystem.Symbol) + ": not resolvable (" +
                          string.Join(", ", subsystem.Libraries) + ")");
                return false;
            }

            try
            {
                switch (subsystem.Shape)
                {
                    case Shape.Domain:
                        // Registering under the engine's own domain name would
                        // collide with the one ewk_init registers a moment later,
                        // and the failure being tested for — the domain table
                        // refusing another entry — is the same either way.
                        var register = (RegisterDomainDelegate)Marshal.GetDelegateForFunctionPointer(
                            symbol, typeof(RegisterDomainDelegate));
                        result = register("overscan-bisect", null);

                        // This one alone signals failure with a negative value,
                        // and a valid domain id may legitimately be 0.
                        Lines.Add(Pad(subsystem.Symbol) + ": " +
                                  (result < 0 ? "REFUSED (" + result + ")" : "ok (domain " + result + ")"));
                        if (result < 0 && Culprit == null)
                        {
                            Culprit = subsystem.Symbol;
                        }

                        return true;

                    case Shape.OneString:
                        var withArgument = (InitStringDelegate)Marshal.GetDelegateForFunctionPointer(
                            symbol, typeof(InitStringDelegate));
                        result = withArgument(null);
                        break;

                    default:
                        var plain = (InitDelegate)Marshal.GetDelegateForFunctionPointer(
                            symbol, typeof(InitDelegate));
                        result = plain();
                        break;
                }
            }
            catch (Exception ex)
            {
                Lines.Add(Pad(subsystem.Symbol) + ": threw " + ex.GetType().Name);
                return false;
            }

            Lines.Add(Pad(subsystem.Symbol) + ": " +
                      (result > 0 ? "ok (refcount " + result + ")" : "REFUSED (returned 0)"));
            if (result == 0 && Culprit == null)
            {
                Culprit = subsystem.Symbol;
            }

            return true;
        }

        private static IntPtr Resolve(Subsystem subsystem)
        {
            foreach (string library in subsystem.Libraries)
            {
                try
                {
                    dlerror();
                    IntPtr handle = dlopen(library, RtldNow | RtldGlobal);
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }

                    IntPtr symbol = dlsym(handle, subsystem.Symbol);
                    if (symbol != IntPtr.Zero)
                    {
                        return symbol;
                    }
                }
                catch (Exception)
                {
                    // Next candidate soname.
                }
            }

            return IntPtr.Zero;
        }

        private static string Pad(string name)
        {
            return name.Length >= 24 ? name : name + new string(' ', 24 - name.Length);
        }

        /// <summary>The whole ladder as one block, for the diagnostics page.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            foreach (string line in Lines)
            {
                sb.Append("  ").Append(line).Append('\n');
            }

            return sb.Length == 0 ? "  (not checked)\n" : sb.ToString();
        }
    }
}
