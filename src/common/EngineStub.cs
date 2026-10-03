using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Tizen.Applications;

namespace Overscan
{
    /// <summary>
    /// Puts a stand-in <c>libprivileged-service-client.so</c> into the process before
    /// the engine goes looking for the real one — issue #105's fix, if it is one.
    ///
    /// The wall on Samsung's retail firmware from 5.5 through 6.0 (issues #17, #95,
    /// #105) is one library: <c>libchromium-impl.so</c> needs
    /// <c>libprivileged-service-client.so</c>, and an app may not open that file —
    /// <c>open(O_RDONLY)</c> is EPERM, above Smack, lifted by no manifest privilege.
    /// The implementation itself is readable, and the AU7200 (#105) is the first set
    /// to let us map a library of our own executable, so the route written down in
    /// docs/INTERNALS.md (*What is left on the Q80*) is open there: ship a library
    /// under that soname, dlopen it <c>RTLD_GLOBAL</c> by absolute path before
    /// <c>ewk_init</c>, and when the loader walks the implementation's DT_NEEDED it
    /// finds the soname already in the process and never goes to /usr/lib for the
    /// file it may not open. <c>build-e1a648d</c>'s import census said what the stub
    /// has to export: four plain C functions (<c>PS_Mknod</c>, <c>PS_Mount</c>,
    /// <c>PS_Umount</c>, <c>PS_ErrorToString</c>), no data, nothing C++ — see
    /// <see cref="EngineImports"/> and <c>tools/elfprobe/psstub.s</c>, which is the
    /// whole library.
    ///
    /// When it loads is the whole safety of it. **Never on a set where the engine
    /// starts on its own:** the gate is <see cref="NativeProbe.EngineFailedHere"/>,
    /// the ledger that only comes into existence on a launch whose engine failed, so
    /// a working set never has a stub shadowing its real library. And **at most twice
    /// on a set where it does not help:** a launch that loads the stub and then never
    /// comes back from the engine (the Q80's shape — the process ending a second
    /// after <c>ewk_init</c>) leaves a <c>trying</c> line with no <c>came back</c>
    /// behind it in <see cref="LedgerFile"/>; two of those and the stub is withheld,
    /// the failure screen returns, and the report says so. The same rule the
    /// ladder's rungs live by (<c>NativeProbe.FatalAfter</c>), for the same reason: a
    /// diagnostic that kills every launch is a brick, and a reporter cannot tell a
    /// brick from a dead set.
    ///
    /// Everything here is best-effort and under a <see cref="Deadline"/>: a stub that
    /// cannot be loaded is recorded by name and the engine is asked without it.
    /// </summary>
    internal static class EngineStub
    {
        /// <summary>The soname the engine's implementation needs, and the file's name in <c>res/</c>.</summary>
        public const string Soname = "libprivileged-service-client.so";

        /// <summary>One symbol it exports, to prove the handle is the stub and not the firmware's file.</summary>
        private const string ProofSymbol = "PS_Mount";

        /// <summary>
        /// Where the attempts are kept, in the one directory that survives a launch.
        /// One line per event: <c>trying</c> before the dlopen, <c>came back&lt;tab&gt;why</c>
        /// once the engine has answered with the stub in. A <c>trying</c> with no
        /// <c>came back</c> behind it is a launch that ended with the stub in.
        /// </summary>
        private const string LedgerFile = "engine-stub.txt";

        /// <summary>How many launches may end with the stub in before it is withheld.</summary>
        private const int GiveUpAfter = 2;

        private const int RtldNow = 0x0002;
        private const int RtldGlobal = 0x0100;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private static bool _asked;
        private static string _ledger;

        /// <summary>One line for the report's header.</summary>
        public static string Summary { get; private set; } = "(not asked yet)";

        /// <summary>True once the stub is in the process under its soname.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>
        /// Loads the stub if this install is one it is for. Call after the import
        /// census and before the engine; a second call does nothing.
        /// </summary>
        public static void Preload()
        {
            if (_asked)
            {
                return;
            }

            _asked = true;
            try
            {
                Attempt();
            }
            catch (Exception ex)
            {
                Summary = "failed before the call — " + ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("engine stub: " + Summary);
            }
        }

        /// <summary>
        /// The engine has answered — started, or failed in the ordinary way — with the
        /// stub in the process. Written down so the next launch knows this one came
        /// back; a launch that dies inside the engine never gets here, which is the
        /// point. Nothing to write when the stub was not loaded.
        /// </summary>
        public static void EngineAnswered(string what)
        {
            if (!Loaded)
            {
                return;
            }

            Record("came back\t" + (what ?? "(no word)"));
        }

        private static void Attempt()
        {
            string file = Locate();
            if (file == null)
            {
                Summary = "not shipped in this package";
                return;
            }

            if (!NativeProbe.EngineFailedHere())
            {
                // The ordinary case on every set this app works on. Not a trail line:
                // nothing happened, and the header says why.
                Summary = "shipped, not loaded — the engine has not failed on this install";
                return;
            }

            string data = DataDirectory();
            _ledger = string.IsNullOrEmpty(data) ? null : Path.Combine(data, LedgerFile);
            int abandoned = Abandoned();
            if (abandoned >= GiveUpAfter)
            {
                Summary = "withheld — " + abandoned.ToString(CultureInfo.InvariantCulture) +
                          " launches never came back from the engine with it in";
                Breadcrumbs.Drop("engine stub: " + Summary);
                return;
            }

            long size = -1;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception)
            {
                // The size is for the reader; the dlopen is the measurement.
            }

            Breadcrumbs.Drop("engine stub: loading " + file +
                             (size >= 0 ? " (" + size.ToString(CultureInfo.InvariantCulture) + " bytes)" : string.Empty) +
                             " RTLD_NOW|RTLD_GLOBAL" +
                             (abandoned > 0
                                 ? " — attempt " + (abandoned + 1).ToString(CultureInfo.InvariantCulture) +
                                   ", the last launch with it in never came back"
                                 : string.Empty));
            Record("trying");

            string error = null;
            string answer = Deadline.Run(delegate
            {
                dlerror();
                IntPtr handle = dlopen(file, RtldNow | RtldGlobal);
                if (handle == IntPtr.Zero)
                {
                    IntPtr message = dlerror();
                    error = message == IntPtr.Zero ? "dlopen failed without a message" : Marshal.PtrToStringAnsi(message);
                    return "refused";
                }

                return dlsym(handle, ProofSymbol) != IntPtr.Zero
                    ? "loaded"
                    : "loaded, but " + ProofSymbol + " did not resolve from it";
            });

            if (answer == "loaded")
            {
                Loaded = true;
                Summary = "loaded from " + file + " — the loader has " + Soname +
                          " from us; the engine is asked with it in";
            }
            else if (answer == Deadline.Missed)
            {
                // The dlopen itself parked. The trying line stays unanswered, which
                // is what the next launch should count.
                Summary = "DID NOT RETURN — dlopen of " + file;
            }
            else
            {
                Summary = "refused — " + (error ?? answer);
                Record("came back\tnot loaded: " + Summary);
            }

            Breadcrumbs.Drop("engine stub: " + Summary);
        }

        /// <summary>Launches that loaded the stub and never wrote that the engine answered.</summary>
        private static int Abandoned()
        {
            if (_ledger == null)
            {
                return 0;
            }

            int trying = 0, cameBack = 0;
            string read = Deadline.Run(delegate
            {
                if (!File.Exists(_ledger))
                {
                    return "ok";
                }

                foreach (string line in File.ReadAllLines(_ledger))
                {
                    if (line.StartsWith("trying", StringComparison.Ordinal))
                    {
                        trying++;
                    }
                    else if (line.StartsWith("came back", StringComparison.Ordinal))
                    {
                        cameBack++;
                    }
                }

                return "ok";
            });

            if (read != "ok")
            {
                // A ledger that will not open is written off: the stub is tried as if
                // for the first time, and the trail says the books were unreadable.
                Breadcrumbs.Drop("engine stub: ledger unreadable (" + read + ") — counting from zero");
                return 0;
            }

            return Math.Max(0, trying - cameBack);
        }

        private static void Record(string line)
        {
            if (_ledger == null)
            {
                return;
            }

            string wrote = Deadline.Run(delegate
            {
                File.AppendAllText(_ledger, line + "\n");
                return "ok";
            });

            if (wrote != "ok")
            {
                Breadcrumbs.Drop("engine stub: could not write '" + line + "' to the ledger (" + wrote + ")");
            }
        }

        /// <summary>The stub's path in <c>res/</c>, or null when this package does not ship one.</summary>
        private static string Locate()
        {
            try
            {
                var info = Application.Current == null ? null : Application.Current.DirectoryInfo;
                string resource = info == null ? null : info.Resource;
                if (string.IsNullOrEmpty(resource))
                {
                    return null;
                }

                string path = Path.Combine(resource, Soname);
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DataDirectory()
        {
            try
            {
                var info = Application.Current == null ? null : Application.Current.DirectoryInfo;
                return info == null ? null : info.Data;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
