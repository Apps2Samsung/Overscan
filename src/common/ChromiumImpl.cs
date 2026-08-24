using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// Loads the engine's *implementation* library, which is what actually fails.
    ///
    /// libchromium-ewk.so is a thin shim: <c>ewk_init()</c> brings the nine EFL
    /// subsystems up and then dlopens the 48 MB
    /// <c>/usr/share/chromium-efl/lib/libchromium-impl.so</c> that holds all of
    /// chromium. Issue #17 (Q80, Tizen 5.5) pinned the fault there, by a line
    /// nothing in this repository prints:
    ///
    ///     engine said: -rw-r--r-- 1 root root 48767918 2026-03-31 16:47
    ///                  /usr/share/chromium-efl/lib/libchromium-impl.so
    ///
    /// That is a busybox <c>ls -l</c>, captured out of our own redirected stderr
    /// during <c>ewk_init</c>. The shim runs it — through <c>system()</c>, which is
    /// why a child's output reached our file at all — as evidence on the one path
    /// where it needs evidence: its dlopen of the implementation failed, and it
    /// lists the file to show it is nonetheless there. The <c>dlerror()</c> beside
    /// that <c>ls</c> goes through chromium's own logging, which on a TV means dlog,
    /// which a retail set will not let us read. Hence: do the same dlopen
    /// ourselves, where <c>dlerror()</c> is a string we can put on the screen.
    ///
    /// All nine EFL inits came up on that set (see <see cref="EflSubsystems"/>), so
    /// this is the remaining step in <c>ewk_init</c> that can return 0, and the
    /// engine's own <c>ls</c> is a direct admission of which one.
    ///
    /// This is also a candidate *fix*, not only a diagnostic. The likeliest reason
    /// a library that exists will not load is a dependency the loader cannot find:
    /// the built-in browser is launched with the engine's directory on
    /// <c>LD_LIBRARY_PATH</c> and a .NET app process is not, and
    /// <c>LD_LIBRARY_PATH</c> is read once at process start, so setting it now would
    /// do nothing. Absolute-path dlopens do work, though — so
    /// <see cref="Preload"/> reads the missing soname back out of
    /// <c>dlerror()</c>, finds that file itself, opens it RTLD_GLOBAL, and retries.
    /// Once a library is in the process under its soname, the shim's own dlopen a
    /// moment later matches it and succeeds.
    ///
    /// If instead the implementation loads for us and <c>ewk_init</c> still returns
    /// 0, that eliminates this whole theory, which is worth as much as confirming
    /// it.
    /// </summary>
    internal static class ChromiumImpl
    {
        private const int RtldLazy = 1;
        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        /// <summary>
        /// Bound on the dependency chase. Each round satisfies exactly one missing
        /// soname, and a real chain is two or three deep; a number this size only
        /// stops a pathological loop from hanging start-up.
        /// </summary>
        private const int MaxRounds = 16;

        /// <summary>Where the implementation has been seen. The first is the path the Q80's engine listed.</summary>
        private static readonly string[] Candidates =
        {
            "/usr/share/chromium-efl/lib/libchromium-impl.so",
            "/usr/lib/libchromium-impl.so",
            "/usr/share/chromium-efl/libchromium-impl.so",
        };

        /// <summary>
        /// Where a missing dependency is looked for. The engine's own directory
        /// comes first: that is the one the loader does *not* search for us, and so
        /// the one a missing soname is most likely to be sitting in.
        /// </summary>
        private static readonly string[] SearchDirectories =
        {
            "/usr/share/chromium-efl/lib",
            "/usr/share/chromium-efl",
            "/usr/lib/chromium-efl",
            "/usr/lib",
            "/lib",
        };

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        private static readonly List<string> Lines = new List<string>();

        /// <summary>Sonames already chased, so a chain cannot circle.</summary>
        private static readonly HashSet<string> Attempted = new HashSet<string>();

        private static bool _preloaded;
        private static bool _explained;

        /// <summary>One line for the diagnostics header.</summary>
        public static string Summary { get; private set; } = "(not probed)";

        /// <summary>True when the implementation is in this process.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>The path that loaded, or the one that refused to.</summary>
        public static string Implementation { get; private set; }

        /// <summary>
        /// Opens the implementation before <c>ewk_init</c> gets there, chasing any
        /// dependency the loader cannot find on its own.
        ///
        /// Call this after the EFL ladder and before <c>Chromium.Initialize()</c>:
        /// the shim would dlopen the same file at the same point, so nothing runs
        /// earlier than it otherwise would — it just runs where we can read the
        /// error.
        ///
        /// Never throws. A diagnostic that can take the process down is worse than
        /// no diagnostic.
        /// </summary>
        public static void Preload()
        {
            if (_preloaded)
            {
                return;
            }

            _preloaded = true;

            try
            {
                string path = Locate();
                if (path == null)
                {
                    Summary = "not present (searched " + Candidates.Length + " paths)";
                    Lines.Add("libchromium-impl.so is not at any known path");
                    return;
                }

                Implementation = path;
                Lines.Add("implementation: " + path + " (" + Describe(path) + ")");

                string error;
                if (Open(path, 0, out error))
                {
                    Loaded = true;
                    Summary = "loaded from " + path;
                    Lines.Add("dlopen ok — the implementation is in the process");
                    return;
                }

                Summary = "REFUSED — " + Brief(error);
                Lines.Add("dlopen failed: " + error);

                // "Operation not permitted" names the library the loader would not
                // open but says nothing about why, and the three reasons it can mean
                // want three different fixes. Ask the set. See SmackWall.
                string blocked = SmackWall.BlockedSoname(error);
                if (blocked != null)
                {
                    Lines.Add("blocked by permission, not by a missing file — probing " + blocked);
                    foreach (string line in SmackWall.Investigate(blocked))
                    {
                        Lines.Add(line);
                    }

                    Summary = "REFUSED — " + blocked + ": " + SmackWall.Summary;
                }
            }
            catch (Exception ex)
            {
                Summary = "probe threw " + ex.GetType().Name;
                Lines.Add("probe threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The rest of the evidence, gathered only once <c>ewk_init</c> has already
        /// failed for good.
        ///
        /// Deliberately after the fact. The RTLD_LAZY retry here is the one probe
        /// that could make things worse — a library that loads lazily and is
        /// missing a symbol will fault the first time that symbol is called, so
        /// leaving it in the process ahead of a *working* init would trade a clean
        /// refusal for a crash. Running it after both init attempts have returned 0
        /// costs nothing, and tells us whether the fault is an unresolved symbol
        /// (fails RTLD_NOW, loads RTLD_LAZY) or the file genuinely not opening.
        /// </summary>
        public static void Explain()
        {
            if (_explained)
            {
                return;
            }

            _explained = true;

            try
            {
                Lines.Add("LD_LIBRARY_PATH: " +
                          (Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "(unset)"));

                if (Implementation != null)
                {
                    Lines.Add("readable by us: " + Readable(Implementation));

                    if (!Loaded)
                    {
                        // A bare RTLD_LAZY, without RTLD_GLOBAL: if it does load,
                        // keep it out of the global symbol scope so it cannot be
                        // bound to by anything still running.
                        string error;
                        bool lazy = TryOpen(Implementation, RtldLazy, out error);
                        Lines.Add(lazy
                            ? "RTLD_LAZY: loads — so the fault is a symbol the engine " +
                              "resolves at bind time, not the file failing to open"
                            : "RTLD_LAZY: also fails — " + error);
                    }
                }

                ListDirectory("/usr/share/chromium-efl/lib");
            }
            catch (Exception ex)
            {
                Lines.Add("explain threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// dlopens <paramref name="path"/>, satisfying one missing dependency per
        /// round and retrying, until it loads or until something other than a
        /// missing file stops it.
        /// </summary>
        private static bool Open(string path, int depth, out string error)
        {
            for (int round = 0; round < MaxRounds; round++)
            {
                if (TryOpen(path, RtldNow | RtldGlobal, out error))
                {
                    return true;
                }

                string missing = MissingSoname(error);
                if (missing == null)
                {
                    // "undefined symbol", "Operation not permitted", "failed to map
                    // segment" — a wall, not a lookup we can help with. Preload
                    // takes the permission ones apart afterwards.
                    return false;
                }

                if (!Attempted.Add(missing))
                {
                    error = missing + " is still not resolvable after being loaded — " + error;
                    return false;
                }

                string dependency = Find(missing);
                if (dependency == null)
                {
                    error = missing + " is not on this firmware (searched " +
                            string.Join(", ", SearchDirectories) + ")";
                    return false;
                }

                Lines.Add(Indent(depth) + "needs " + missing + " -> " + dependency);

                string inner;
                if (!Open(dependency, depth + 1, out inner))
                {
                    error = missing + " would not load — " + inner;
                    return false;
                }

                Lines.Add(Indent(depth) + "loaded " + dependency);
            }

            error = "gave up after " + MaxRounds + " dependency rounds";
            return false;
        }

        private static bool TryOpen(string path, int mode, out string error)
        {
            try
            {
                dlerror();
                IntPtr handle = dlopen(path, mode);
                if (handle != IntPtr.Zero)
                {
                    error = null;
                    return true;
                }

                error = Marshal.PtrToStringAnsi(dlerror()) ?? "(no dlerror)";
                return false;
            }
            catch (Exception ex)
            {
                error = "dlopen threw " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// The soname out of a glibc "cannot open shared object file" message, or
        /// null for any other failure.
        ///
        /// The message names the library that is *needed*, never the one that needs
        /// it: "libfoo.so.1: cannot open shared object file: No such file or
        /// directory". Only the No-such-file form is chased — the same sentence
        /// ending in "Operation not permitted" is a permission wall, and no amount
        /// of searching moves it. <see cref="SmackWall.BlockedSoname"/> picks that
        /// half up and establishes which permission.
        /// </summary>
        private static string MissingSoname(string error)
        {
            if (error == null)
            {
                return null;
            }

            int at = error.IndexOf(": cannot open shared object file", StringComparison.Ordinal);
            if (at <= 0 || error.IndexOf("No such file", StringComparison.Ordinal) < 0)
            {
                return null;
            }

            string name = error.Substring(0, at).Trim();
            int slash = name.LastIndexOf('/');
            if (slash >= 0)
            {
                name = name.Substring(slash + 1);
            }

            return name.Length == 0 ? null : name;
        }

        private static string Locate()
        {
            foreach (string candidate in Candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // Unreadable parent directory; try the next path.
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a soname on disk. The versioned-suffix pass is there because a
        /// firmware image may ship libfoo.so.1.2.3 without the libfoo.so.1 symlink
        /// the loader wants — in which case dlopening the real file by absolute
        /// path still registers it under the soname in its own header.
        /// </summary>
        private static string Find(string soname)
        {
            foreach (string directory in SearchDirectories)
            {
                try
                {
                    string direct = Path.Combine(directory, soname);
                    if (File.Exists(direct))
                    {
                        return direct;
                    }

                    string[] versioned = Directory.GetFiles(directory, soname + ".*");
                    if (versioned.Length > 0)
                    {
                        Array.Sort(versioned, StringComparer.Ordinal);
                        return versioned[versioned.Length - 1];
                    }
                }
                catch (Exception)
                {
                    // Missing or unreadable directory; try the next.
                }
            }

            return null;
        }

        private static string Describe(string path)
        {
            try
            {
                var file = new FileInfo(path);
                return file.Length.ToString(CultureInfo.InvariantCulture) + " bytes";
            }
            catch (Exception ex)
            {
                return "cannot stat: " + ex.GetType().Name;
            }
        }

        /// <summary>
        /// Whether this process may read the file at all. dlopen needs read access;
        /// the <c>ls -l</c> the engine printed needs only the directory. So a file
        /// that lists but will not open is the signature of a SMACK label we are not
        /// allowed to touch, and that is a different answer from a missing
        /// dependency.
        /// </summary>
        private static string Readable(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite))
                {
                    return stream.ReadByte() >= 0 ? "yes" : "opens but is empty";
                }
            }
            catch (Exception ex)
            {
                return "NO — " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void ListDirectory(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    Lines.Add(directory + ": not a directory");
                    return;
                }

                string[] entries = Directory.GetFiles(directory);
                Array.Sort(entries, StringComparer.Ordinal);
                Lines.Add(directory + ": " + entries.Length + " files");

                // Enough to see what the engine ships beside itself, few enough to
                // read on a TV.
                for (int i = 0; i < entries.Length && i < 24; i++)
                {
                    Lines.Add("  " + Path.GetFileName(entries[i]));
                }

                if (entries.Length > 24)
                {
                    Lines.Add("  … and " + (entries.Length - 24) + " more");
                }
            }
            catch (Exception ex)
            {
                Lines.Add(directory + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Indent(int depth)
        {
            return new string(' ', 2 * (depth + 1));
        }

        private static string Brief(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(no reason given)";
            }

            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length <= 120 ? flat : flat.Substring(0, 120) + "…";
        }

        /// <summary>One line per step, for the breadcrumb trail and the probe.</summary>
        public static IList<string> Detail
        {
            get { return Lines; }
        }

        /// <summary>The whole probe as one block, for the diagnostics page.</summary>
        public static string Dump()
        {
            var sb = new StringBuilder();
            foreach (string line in Lines)
            {
                sb.Append("  ").Append(line).Append('\n');
            }

            return sb.Length == 0 ? "  (not probed)\n" : sb.ToString();
        }
    }
}
