using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Asks what a stub <c>libprivileged-service-client.so</c> would have to provide:
    /// the symbols the engine's implementation imports that no library this process
    /// may load provides.
    ///
    /// Issue #105, an AU7200 on Tizen 6.0, is the first set to pass the third gate of
    /// the stub route (see *What is left on the Q80* in docs/INTERNALS.md). The Q80
    /// refused to map any file we ship executable, so a stub had nowhere to live and
    /// #17 was closed on it; the AU7200's `build-3996334` report maps `libovprobe.so`
    /// executable and dlopens it in all five locations, on one launch, in 243 ms. The
    /// engine wall on that set is otherwise the Q80's line for line:
    /// `libchromium-impl.so` refused on `libprivileged-service-client.so: Operation
    /// not permitted`, with the implementation file itself readable by us.
    ///
    /// So the route is: ship a stub under that soname, dlopen it by absolute path
    /// with RTLD_GLOBAL before the implementation, and the loader satisfies the
    /// implementation's DT_NEEDED from the soname already in the process instead of
    /// going to /usr/lib for it. What the stub must export is the one thing we cannot
    /// read off the firmware, because the real library is the file it will not let us
    /// open. But the implementation is readable, and the loader can answer the rest:
    ///
    /// 1. Read the implementation's dynamic section: its DT_NEEDED list and every
    ///    undefined dynamic symbol (ELF32 on the set; ELF64 too, for the harness).
    /// 2. dlopen every DT_NEEDED library, recording each one that is refused and why.
    /// 3. Look every undefined symbol up in the process and in each library that did
    ///    load. Whatever is still unresolved is what the stub has to provide, by name.
    ///
    /// Each step past managed code goes out under a <see cref="Deadline"/>, because
    /// a call this firmware objects to is parked rather than refused.
    ///
    /// Where it runs was decided by the `build-283f3e9` report (2026-10-02). That build
    /// put the census on the post-failure probe thread, behind the ladder's walk and
    /// ahead of the permission investigation, and the census never ran: the launch
    /// ended within five seconds of `ENGINE FAILURE` — no heartbeat tick, no deadline
    /// miss — with the thread still on the walk's first repeatable rung. That is the
    /// Q80's shape exactly (see <c>NativeProbe.StartEarlyIfUnfinished</c>): on these
    /// sets a launch whose engine has failed is over before anything queued behind
    /// the failure gets a turn, and the second launch of the same build never reached
    /// the UI at all. So, like the walk, the census goes **ahead of the engine** on
    /// an install whose ledger already says the engine fails here
    /// (<c>NativeProbe.EngineFailedHere</c>), after the walk if one is due, and the
    /// engine waits for it, bounded. On every set this app works on there is no
    /// ledger and none of this runs. The post-failure call is still made, for the
    /// first failure on an install, and finds the census already taken on the rest.
    /// Its lines go to the trail as they are established, because the page that
    /// shows them is served by the *next* launch, before its UI exists.
    ///
    /// How to read it: a short list of plain C function names is a no-op stub,
    /// buildable with the same toolchain recipe as `libovprobe.so`. C++ names (`_Z…`)
    /// or data objects are a stub that has to do real work. A second refused library
    /// means the stub has to stand in for two. Weak undefined symbols are counted and
    /// set aside: the loader does not need them.
    /// </summary>
    internal static class EngineImports
    {
        private const int RtldLazy = 0x0001;
        private const int RtldLocal = 0x0000;

        /// <summary>How many unresolved names the trail carries; the dump has them all.</summary>
        private const int TrailNames = 40;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        private static readonly string[] Candidates =
        {
            "/usr/share/chromium-efl/lib/libchromium-impl.so",
            "/usr/lib/libchromium-impl.so",
            "/usr/share/chromium-efl/libchromium-impl.so",
        };

        private static readonly object Gate = new object();
        private static readonly List<string> Lines = new List<string>();
        private static bool _ran;

        /// <summary>Set whenever no census is running; reset while one is.</summary>
        private static readonly ManualResetEvent Finished = new ManualResetEvent(true);

        /// <summary>One line for the diagnostics header.</summary>
        public static string Summary { get; private set; } = "(not asked)";

        /// <summary>The DT_NEEDED sonames the loader refused, with the loader's words.</summary>
        public static IList<string> Refused { get; private set; } = new List<string>();

        /// <summary>The imports no loadable library provides — what a stub must export.</summary>
        public static IList<string> Unresolved { get; private set; } = new List<string>();

        /// <summary>The whole census, for the full report.</summary>
        public static string Dump()
        {
            lock (Gate)
            {
                return Lines.Count == 0 ? "(not asked)\n" : string.Join("\n", Lines.ToArray()) + "\n";
            }
        }

        private static void Note(string line)
        {
            lock (Gate)
            {
                Lines.Add(line);
            }
        }

        private static void Trace(string line)
        {
            Note(line);
            Breadcrumbs.Drop("engine imports: " + line);
        }

        /// <summary>
        /// Runs the census once. <paramref name="implementation"/> is the path
        /// <c>ChromiumImpl</c> found, or null to look in the known places;
        /// <paramref name="blocked"/> is the soname the loader refused, for the summary.
        /// Never throws: a diagnostic has nobody to throw to.
        /// </summary>
        public static void Census(string implementation, string blocked)
        {
            if (Claim())
            {
                Take(implementation, blocked);
            }
        }

        /// <summary>
        /// Takes the one census this process gets, for whichever caller is first;
        /// false for every caller after. The wait event is reset here, under the
        /// same lock, so a caller that starts the census on a thread and then waits
        /// cannot see the event still set from before the thread got going.
        /// </summary>
        private static bool Claim()
        {
            lock (Gate)
            {
                if (_ran)
                {
                    return false;
                }

                _ran = true;
                Finished.Reset();
                return true;
            }
        }

        private static void Take(string implementation, string blocked)
        {
            try
            {
                Run(implementation, blocked);
            }
            catch (Exception ex)
            {
                Summary = "threw " + ex.GetType().Name + ": " + ex.Message;
                Trace(Summary);
            }
            finally
            {
                try
                {
                    Finished.Set();
                }
                catch (Exception)
                {
                    // Nobody to tell.
                }
            }
        }

        /// <summary>
        /// <see cref="Census"/> on a thread of its own, for the launch that runs it
        /// ahead of the engine: the caller waits with <see cref="WaitForCensus"/>,
        /// bounded, and a census still going when the bound passes carries on while
        /// the engine is asked — its lines reach the trail either way. Claimed here,
        /// before the thread exists, so the post-failure call finds it taken.
        /// </summary>
        public static void RunInBackground(string implementation, string blocked)
        {
            if (!Claim())
            {
                return;
            }

            try
            {
                var thread = new Thread(delegate () { Take(implementation, blocked); });
                thread.IsBackground = true;
                thread.Name = "engine-imports";
                thread.Start();
            }
            catch (Exception ex)
            {
                // A set that will not give us a thread gets asked the dangerous way.
                Trace("no thread (" + ex.GetType().Name + "), taking the census inline");
                Take(implementation, blocked);
            }
        }

        /// <summary>
        /// Waits for a census started by <see cref="RunInBackground"/> to finish, for
        /// at most <paramref name="timeoutMs"/>. True if it did (or none is running).
        /// </summary>
        public static bool WaitForCensus(int timeoutMs)
        {
            try
            {
                return Finished.WaitOne(timeoutMs);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Run(string implementation, string blocked)
        {
            string path = implementation;
            if (path == null)
            {
                foreach (string candidate in Candidates)
                {
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        break;
                    }
                }
            }

            if (path == null)
            {
                Summary = "libchromium-impl.so is not at any known path";
                Trace(Summary);
                return;
            }

            Summary = "reading " + path;
            Trace("reading " + path);

            Elf elf = null;
            string parsed = Deadline.Run(delegate { elf = Parse(path); return "ok"; }, 15000);
            if (parsed != "ok" || elf == null)
            {
                Summary = "reading " + path + ": " + parsed;
                Trace(Summary);
                return;
            }

            int weak = 0;
            foreach (Import import in elf.Imports)
            {
                if (import.Weak)
                {
                    weak++;
                }
            }

            Trace("ELF" + elf.Class + (elf.Soname == null ? string.Empty : " " + elf.Soname) + ": " +
                  elf.Needed.Count + " needed, " + elf.Imports.Count + " imports (" + weak + " weak)");
            foreach (string soname in elf.Needed)
            {
                Note("  needed: " + soname);
            }

            // Step 2: every DT_NEEDED, by soname first (the loader's own search) and
            // then by path in the engine's directories, which the loader does not
            // search for us. One refusal line each; the ones that load are not news.
            var handles = new List<KeyValuePair<string, IntPtr>>();
            var refused = new List<string>();
            string directory = Path.GetDirectoryName(path) ?? "/usr/lib";
            foreach (string soname in elf.Needed)
            {
                IntPtr handle = IntPtr.Zero;
                string error = null;
                string answer = Deadline.Run(delegate
                {
                    handle = Open(soname, out error);
                    if (handle == IntPtr.Zero && IsNotFound(error))
                    {
                        string here = Path.Combine(directory, soname);
                        if (File.Exists(here))
                        {
                            handle = Open(here, out error);
                        }
                    }

                    return handle == IntPtr.Zero ? "refused" : "loaded";
                });

                if (answer == "loaded")
                {
                    handles.Add(new KeyValuePair<string, IntPtr>(soname, handle));
                    Note("  loaded: " + soname);
                    continue;
                }

                string why = answer == Deadline.Missed ? Deadline.Missed : (error ?? answer);
                refused.Add(soname + " — " + why);
                Trace("refused " + soname + " — " + why);
            }

            // Step 3: the process first (libc and everything already in it), then
            // each library that loaded. Weak imports are the loader's business, not
            // a stub's; they are set aside rather than asked.
            var unresolved = new List<string>();
            var providers = new Dictionary<string, int>();
            string resolved = Deadline.Run(delegate
            {
                foreach (Import import in elf.Imports)
                {
                    if (import.Weak)
                    {
                        continue;
                    }

                    string provider = null;
                    if (dlsym(IntPtr.Zero, import.Name) != IntPtr.Zero)
                    {
                        provider = "(process)";
                    }
                    else
                    {
                        foreach (KeyValuePair<string, IntPtr> pair in handles)
                        {
                            if (dlsym(pair.Value, import.Name) != IntPtr.Zero)
                            {
                                provider = pair.Key;
                                break;
                            }
                        }
                    }

                    if (provider == null)
                    {
                        unresolved.Add(import.Name + " (" + import.Kind + ")");
                    }
                    else
                    {
                        int n;
                        providers.TryGetValue(provider, out n);
                        providers[provider] = n + 1;
                    }
                }

                return "ok";
            }, 30000);

            if (resolved != "ok")
            {
                Summary = "resolving imports: " + resolved;
                Trace(Summary);
                return;
            }

            foreach (KeyValuePair<string, int> pair in providers)
            {
                Note("  provides " + pair.Value.ToString(CultureInfo.InvariantCulture) + ": " + pair.Key);
            }

            int cpp = 0;
            foreach (string name in unresolved)
            {
                if (name.StartsWith("_Z", StringComparison.Ordinal))
                {
                    cpp++;
                }
            }

            Trace(unresolved.Count + " unresolved" + (cpp > 0 ? " (" + cpp + " C++)" : string.Empty) +
                  (unresolved.Count > 0 ? " — what a stub must provide" : " — nothing for a stub to provide"));
            for (int i = 0; i < unresolved.Count; i++)
            {
                if (i < TrailNames)
                {
                    Trace("  import: " + unresolved[i]);
                }
                else
                {
                    Note("  import: " + unresolved[i]);
                }
            }

            if (unresolved.Count > TrailNames)
            {
                Trace("  … " + (unresolved.Count - TrailNames) + " more in the full report");
            }

            Refused = refused;
            Unresolved = unresolved;

            var summary = new StringBuilder();
            summary.Append(elf.Needed.Count).Append(" needed");
            if (refused.Count > 0)
            {
                summary.Append(" (").Append(refused.Count).Append(" refused: ");
                for (int i = 0; i < refused.Count; i++)
                {
                    if (i > 0)
                    {
                        summary.Append(", ");
                    }

                    string soname = refused[i];
                    int dash = soname.IndexOf(" — ", StringComparison.Ordinal);
                    summary.Append(dash > 0 ? soname.Substring(0, dash) : soname);
                }

                summary.Append(")");
            }

            summary.Append(", ").Append(elf.Imports.Count).Append(" imports, ")
                   .Append(unresolved.Count).Append(" unresolved");
            if (unresolved.Count > 0 && unresolved.Count <= 6)
            {
                summary.Append(": ").Append(string.Join(", ", unresolved.ToArray()));
            }

            if (blocked != null && refused.Count == 0)
            {
                summary.Append(" — ").Append(blocked).Append(" loaded this time");
            }

            Summary = summary.ToString();
            Trace("done — " + Summary);
        }

        private static IntPtr Open(string file, out string error)
        {
            error = null;
            dlerror();
            IntPtr handle = dlopen(file, RtldLazy | RtldLocal);
            if (handle == IntPtr.Zero)
            {
                IntPtr message = dlerror();
                error = message == IntPtr.Zero ? "dlopen failed without a message" : Marshal.PtrToStringAnsi(message);
            }

            return handle;
        }

        private static bool IsNotFound(string error)
        {
            return error != null && error.IndexOf("No such file", StringComparison.Ordinal) >= 0;
        }

        private sealed class Import
        {
            public string Name;
            public string Kind;
            public bool Weak;
        }

        private sealed class Elf
        {
            public int Class;
            public string Soname;
            public readonly List<string> Needed = new List<string>();
            public readonly List<Import> Imports = new List<Import>();
        }

        /// <summary>
        /// The dynamic section and the dynamic symbol table, off the section headers.
        /// Little-endian only, which both the set and the harness are. Throws on a
        /// file that is not what it claims; the caller turns that into a line.
        /// </summary>
        private static Elf Parse(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                byte[] ident = reader.ReadBytes(16);
                if (ident.Length < 16 || ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
                {
                    throw new InvalidDataException("not an ELF");
                }

                if (ident[5] != 1)
                {
                    throw new InvalidDataException("big-endian ELF, not handled");
                }

                var elf = new Elf();
                elf.Class = ident[4] == 2 ? 64 : 32;
                bool wide = elf.Class == 64;

                reader.ReadUInt16(); // e_type
                reader.ReadUInt16(); // e_machine
                reader.ReadUInt32(); // e_version
                Word(reader, wide);  // e_entry
                Word(reader, wide);  // e_phoff
                long shoff = (long)Word(reader, wide);
                reader.ReadUInt32(); // e_flags
                reader.ReadUInt16(); // e_ehsize
                reader.ReadUInt16(); // e_phentsize
                reader.ReadUInt16(); // e_phnum
                int shentsize = reader.ReadUInt16();
                int shnum = reader.ReadUInt16();
                reader.ReadUInt16(); // e_shstrndx

                if (shoff <= 0 || shnum <= 0)
                {
                    throw new InvalidDataException("no section headers");
                }

                // Section headers: type, link, offset, size and entsize are what the
                // two tables need; the others are read past.
                var sections = new List<Section>();
                for (int i = 0; i < shnum; i++)
                {
                    stream.Seek(shoff + (long)i * shentsize, SeekOrigin.Begin);
                    var section = new Section();
                    reader.ReadUInt32();                       // sh_name
                    section.Type = reader.ReadUInt32();        // sh_type
                    Word(reader, wide);                        // sh_flags
                    Word(reader, wide);                        // sh_addr
                    section.Offset = (long)Word(reader, wide); // sh_offset
                    section.Size = (long)Word(reader, wide);   // sh_size
                    section.Link = (int)reader.ReadUInt32();   // sh_link
                    reader.ReadUInt32();                       // sh_info
                    Word(reader, wide);                        // sh_addralign
                    section.EntSize = (long)Word(reader, wide);
                    sections.Add(section);
                }

                Section dynamic = null, dynsym = null;
                foreach (Section section in sections)
                {
                    if (section.Type == 6 && dynamic == null) { dynamic = section; }
                    if (section.Type == 11 && dynsym == null) { dynsym = section; }
                }

                if (dynamic == null || dynsym == null)
                {
                    throw new InvalidDataException("no dynamic section or symbol table");
                }

                // DT_NEEDED and DT_SONAME, names out of the string table the dynamic
                // section links to.
                Section dynstr = Table(sections, dynamic.Link);
                long count = dynamic.EntSize > 0 ? dynamic.Size / dynamic.EntSize : 0;
                var soname = new List<long>();
                var needed = new List<long>();
                stream.Seek(dynamic.Offset, SeekOrigin.Begin);
                for (long i = 0; i < count; i++)
                {
                    long tag = (long)Word(reader, wide);
                    ulong value = Word(reader, wide);
                    if (tag == 0) { break; }
                    if (tag == 1) { needed.Add((long)value); }
                    if (tag == 14) { soname.Add((long)value); }
                }

                foreach (long offset in needed)
                {
                    elf.Needed.Add(Name(stream, reader, dynstr, offset));
                }

                if (soname.Count > 0)
                {
                    elf.Soname = Name(stream, reader, dynstr, soname[0]);
                }

                // Undefined dynamic symbols: st_shndx == SHN_UNDEF with a name.
                Section symstr = Table(sections, dynsym.Link);
                long symbols = dynsym.EntSize > 0 ? dynsym.Size / dynsym.EntSize : 0;
                var raw = new List<Import>();
                var names = new List<long>();
                for (long i = 1; i < symbols; i++)
                {
                    stream.Seek(dynsym.Offset + i * dynsym.EntSize, SeekOrigin.Begin);
                    long name;
                    byte info;
                    int shndx;
                    if (wide)
                    {
                        name = reader.ReadUInt32();
                        info = reader.ReadByte();
                        reader.ReadByte();           // st_other
                        shndx = reader.ReadUInt16();
                    }
                    else
                    {
                        name = reader.ReadUInt32();
                        reader.ReadUInt32();         // st_value
                        reader.ReadUInt32();         // st_size
                        info = reader.ReadByte();
                        reader.ReadByte();           // st_other
                        shndx = reader.ReadUInt16();
                    }

                    if (shndx != 0 || name == 0)
                    {
                        continue;
                    }

                    int bind = info >> 4, type = info & 0xF;
                    var import = new Import();
                    import.Weak = bind == 2;
                    import.Kind = type == 1 ? "OBJECT" : type == 2 ? "FUNC" : type == 10 ? "IFUNC" : type == 6 ? "TLS" : "type " + type;
                    raw.Add(import);
                    names.Add(name);
                }

                for (int i = 0; i < raw.Count; i++)
                {
                    raw[i].Name = Name(stream, reader, symstr, names[i]);
                    if (raw[i].Name.Length > 0)
                    {
                        elf.Imports.Add(raw[i]);
                    }
                }

                return elf;
            }
        }

        private sealed class Section
        {
            public uint Type;
            public long Offset;
            public long Size;
            public int Link;
            public long EntSize;
        }

        private static Section Table(List<Section> sections, int index)
        {
            if (index < 0 || index >= sections.Count || sections[index].Type != 3)
            {
                throw new InvalidDataException("string table " + index + " is not one");
            }

            return sections[index];
        }

        private static ulong Word(BinaryReader reader, bool wide)
        {
            return wide ? reader.ReadUInt64() : reader.ReadUInt32();
        }

        private static string Name(Stream stream, BinaryReader reader, Section table, long offset)
        {
            if (offset < 0 || offset >= table.Size)
            {
                return string.Empty;
            }

            stream.Seek(table.Offset + offset, SeekOrigin.Begin);
            var bytes = new List<byte>();
            long limit = Math.Min(table.Size - offset, 4096);
            for (long i = 0; i < limit; i++)
            {
                byte b = reader.ReadByte();
                if (b == 0) { break; }
                bytes.Add(b);
            }

            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
