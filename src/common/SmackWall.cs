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
    /// Takes apart a "cannot open shared object file: Operation not permitted".
    ///
    /// That sentence is where two very different faults look identical. Issue #13
    /// hit it on <c>libmarlin.so.0</c> and the answer was a privilege: signing
    /// partner-level with the DRM privileges declared made the same dlopen succeed.
    /// Issue #17 hits it on <c>libprivileged-service-client.so</c>, a dependency of
    /// the engine implementation, on a Q80 that has already cleared the Marlin wall
    /// — so the DRM privileges are in force and something else is refusing.
    ///
    /// The refusal is one of three things, and dlopen says the same word for all of
    /// them:
    ///
    /// * the file cannot be **read** — a Smack label this app may not touch, which
    ///   is what a privilege grants access to and therefore the case a manifest can
    ///   fix;
    /// * the file reads but cannot be **mapped executable** — `mmap(PROT_EXEC)`
    ///   returns EPERM on a `noexec` mount and under an exec-label rule, and no
    ///   privilege in any manifest moves either;
    /// * the file is not **where the loader looked** at all, in which case the word
    ///   is about some other path entirely.
    ///
    /// Guessing between those costs a firmware round-trip per guess, and there is
    /// no published list of Samsung TV partner privileges to guess from:
    /// <c>libprivileged-service-client.so</c> appears in no documentation, no
    /// package, and no source tree we can read. So this asks the set directly —
    /// open it, map it, read its Smack labels, and print ours beside them — and the
    /// answer arrives on the diagnostics page as evidence rather than a theory.
    ///
    /// Everything here is read-only and every call is guarded. A diagnostic that
    /// can take start-up down is worse than no diagnostic — and the first version
    /// of this class was exactly that. On both reporting sets (#13 on a 2018 set,
    /// #17 on the Q80) the trail stopped dead on the line before the probe and the
    /// engine was never even asked to start: something in here does not survive
    /// that firmware, and because the findings were handed back to the caller in
    /// one batch at the end, nothing reached disk to say which call it was. So two
    /// rules now hold, and they are the whole difference:
    ///
    /// * <b>every line is flushed as it is produced</b> (see <see cref="Trace"/>),
    ///   so a native crash names the call that caused it on the next launch;
    /// * <b>the probe never runs before the engine does</b>. It is started from
    ///   <see cref="InvestigateInBackground"/> on a background thread, after
    ///   <c>ewk_init</c> has already failed and the failure screen is up. A hang
    ///   costs a thread and a crash costs a process that had nothing left to do.
    /// </summary>
    internal static class SmackWall
    {
        private const int ORdonly = 0;
        private const int ProtRead = 1;
        private const int ProtExec = 4;
        private const int MapPrivate = 2;

        private const int Eperm = 1;
        private const int Enoent = 2;
        private const int Eacces = 13;

        /// <summary>
        /// Where a blocked soname is looked for. The engine's own directory first:
        /// that is the one the loader does not search for a .NET process, so a file
        /// found only there was never a permission problem to begin with.
        /// </summary>
        private static readonly string[] SearchDirectories =
        {
            "/usr/share/chromium-efl/lib",
            "/usr/share/chromium-efl",
            "/usr/lib/chromium-efl",
            "/usr/lib",
            "/lib",
            "/usr/lib/arm-linux-gnueabi",
        };

        /// <summary>
        /// Libraries whose labels are worth having beside the blocked one. The
        /// engine shim is known to open on this set, so its labels are the shape of
        /// "allowed"; Marlin is the privilege wall we already climbed once.
        /// </summary>
        private static readonly string[] Controls =
        {
            "/usr/lib/libchromium-ewk.so",
            "/usr/share/chromium-efl/lib/libchromium-impl.so",
            "/usr/lib/libmarlin.so.0",
        };

        private static readonly string[] Labels =
        {
            "security.SMACK64",
            "security.SMACK64EXEC",
            "security.SMACK64MMAP",
            "security.SMACK64TRANSMUTE",
        };

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int open(string path, int flags);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern IntPtr read(int fd, byte[] buffer, IntPtr count);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern IntPtr mmap(IntPtr address, IntPtr length, int protection,
                                          int flags, int fd, IntPtr offset);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern int munmap(IntPtr address, IntPtr length);

        [DllImport("libc.so.6", SetLastError = true)]
        private static extern IntPtr getxattr(string path, string name, byte[] value, IntPtr size);

        private static readonly List<string> Lines = new List<string>();

        /// <summary>Targets already taken apart, so a retry cannot double the report.</summary>
        private static readonly HashSet<string> Done = new HashSet<string>();

        /// <summary>
        /// Guards <see cref="Lines"/> and <see cref="Done"/>: the probe runs on its
        /// own thread now and the diagnostics page is served from another one.
        /// </summary>
        private static readonly object Gate = new object();

        private static bool _identified;

        /// <summary>The verdict, or "(not reached)" while nothing has been blocked.</summary>
        public static string Summary { get; private set; } = "(not reached)";

        /// <summary>Every line produced so far, for the probe and the trail.</summary>
        public static IList<string> Detail
        {
            get
            {
                lock (Gate)
                {
                    return Lines.ToArray();
                }
            }
        }

        /// <summary>
        /// Starts the investigation on a background thread and returns at once.
        ///
        /// The one thing this class must never do again is stand between start-up
        /// and the engine. Nothing downstream needs its answer — the answer is for
        /// us, on the next log — so the caller carries on, the failure screen goes
        /// up, and the findings arrive in the trail and on the diagnostics page as
        /// they are established. A probe that hangs now hangs a thread nobody is
        /// waiting on.
        /// </summary>
        public static void InvestigateInBackground(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return;
            }

            try
            {
                var thread = new Thread(delegate ()
                {
                    Investigate(target);
                });

                // Background, so a probe still running cannot keep the process
                // alive after the user has walked away from the failure screen.
                thread.IsBackground = true;
                thread.Name = "smack-probe";
                thread.Start();
            }
            catch (Exception ex)
            {
                // A set that will not give us a thread still gets the verdict, it
                // just gets it the dangerous way.
                Trace("probe: no thread (" + ex.GetType().Name + "), running inline");
                Investigate(target);
            }
        }

        /// <summary>
        /// The soname out of a dlopen refusal that is a *permission* refusal, or
        /// null for anything else.
        ///
        /// The counterpart of <c>ChromiumImpl.MissingSoname</c>, which takes the
        /// same sentence ending in "No such file or directory". These are the two
        /// halves of one message and only ever one of them matches.
        /// </summary>
        public static string BlockedSoname(string error)
        {
            if (error == null)
            {
                return null;
            }

            int at = error.IndexOf(": cannot open shared object file", StringComparison.Ordinal);
            if (at <= 0)
            {
                return null;
            }

            if (error.IndexOf("Operation not permitted", StringComparison.Ordinal) < 0 &&
                error.IndexOf("Permission denied", StringComparison.Ordinal) < 0)
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

        /// <summary>
        /// Establishes what is actually refusing <paramref name="target"/>, which is
        /// either a bare soname out of <see cref="BlockedSoname"/> or an absolute
        /// path.
        ///
        /// Returns only the lines this call added, so a caller with its own
        /// transcript can splice them in; they are kept in <see cref="Detail"/> too.
        /// Calling it twice for the same target adds nothing.
        /// </summary>
        public static IList<string> Investigate(string target)
        {
            var added = new List<string>();

            lock (Gate)
            {
                if (string.IsNullOrEmpty(target) || !Done.Add(target))
                {
                    return added;
                }
            }

            try
            {
                Trace("probe: begin " + target);
                Identify(added);

                Trace("probe: locate " + target);
                string path = target.IndexOf('/') >= 0 ? target : Find(target);
                if (path == null)
                {
                    Summary = target + " is not on this firmware";
                    Add(added, target + ": not found in " +
                               string.Join(", ", SearchDirectories));
                    return added;
                }

                Add(added, "blocked: " + path + " (" + Size(path) + ")");

                Trace("probe: mountinfo");
                Add(added, "  mount: " + MountOf(path));

                Trace("probe: getxattr " + path);
                foreach (string line in Xattrs(path))
                {
                    Add(added, "  " + line);
                }

                Verdict(path, added);

                Add(added, "for comparison");
                foreach (string control in Controls)
                {
                    if (!Exists(control))
                    {
                        continue;
                    }

                    Trace("probe: control " + control);
                    Add(added, "  " + control);
                    Add(added, "    read: " + ReadProbe(control) + "   " + Xattrs(control)[0]);
                }

                Trace("probe: done");
            }
            catch (Exception ex)
            {
                Summary = "smack probe threw " + ex.GetType().Name;
                Add(added, "smack probe threw " + ex.GetType().Name + ": " + ex.Message);
            }

            return added;
        }

        /// <summary>
        /// Which of the three refusals this is. Reading and mapping are asked
        /// separately because they fail for unrelated reasons and only the first is
        /// something a manifest can change.
        /// </summary>
        private static void Verdict(string path, List<string> added)
        {
            int fd = -1;
            try
            {
                Trace("probe: open " + path);
                fd = open(path, ORdonly);
                if (fd < 0)
                {
                    int errno = Marshal.GetLastWin32Error();
                    Add(added, "  open(O_RDONLY): " + Errno(errno));
                    Summary = "READ DENIED (" + Errno(errno) + ") — a privilege could lift this";
                    Add(added, "  verdict: the file cannot be read at all. That is the Marlin " +
                               "shape from issue #13: a label a privilege grants, so the fix is " +
                               "in the manifest.");
                    return;
                }

                Trace("probe: read header");
                var head = new byte[4];
                IntPtr got = read(fd, head, (IntPtr)head.Length);
                bool elf = got.ToInt64() == 4 && head[0] == 0x7f &&
                           head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
                Add(added, "  open(O_RDONLY): ok" + (elf ? ", reads as ELF" : ", first bytes are not ELF"));

                Trace("probe: mmap PROT_READ");
                string readable = MapProbe(fd, ProtRead, "PROT_READ");
                Add(added, "  mmap " + readable);

                // The last of the four calls and the one most likely to be the
                // reason this class killed start-up on both reporting sets: asking
                // a kernel with an exec-label policy to map a page executable is
                // exactly the thing such a policy exists to refuse, and refusing it
                // by signal rather than by errno is a legal way to do that. Traced
                // immediately before, so the next trail says so outright.
                Trace("probe: mmap PROT_READ|PROT_EXEC");
                string executable = MapProbe(fd, ProtRead | ProtExec, "PROT_READ|PROT_EXEC");
                Add(added, "  mmap " + executable);

                if (executable.EndsWith("ok", StringComparison.Ordinal))
                {
                    Summary = "opens and maps here — the loader's refusal is about some other path";
                    Add(added, "  verdict: this process can read and map this file. Whatever the " +
                               "loader could not open, it was not this copy.");
                    return;
                }

                Summary = "EXEC MAPPING DENIED — no privilege lifts this";
                Add(added, "  verdict: the file reads but will not map executable. That is a " +
                           "noexec mount or an exec-label rule, and no manifest privilege " +
                           "changes either — see the mount line above.");
            }
            finally
            {
                if (fd >= 0)
                {
                    try
                    {
                        close(fd);
                    }
                    catch (Exception)
                    {
                        // Nothing left to do with it either way.
                    }
                }
            }
        }

        /// <summary>Who this process is, in the terms the refusal is written in.</summary>
        private static void Identify(List<string> added)
        {
            if (_identified)
            {
                return;
            }

            _identified = true;
            Add(added, "our smack label: " + FirstLine("/proc/self/attr/current"));
            Add(added, "our capabilities: " + Field("/proc/self/status", "CapEff:"));
        }

        private static string MapProbe(int fd, int protection, string name)
        {
            IntPtr length = (IntPtr)4096;
            IntPtr address = mmap(IntPtr.Zero, length, protection, MapPrivate, fd, IntPtr.Zero);
            if (address == IntPtr.Zero || address.ToInt64() == -1)
            {
                return name + ": " + Errno(Marshal.GetLastWin32Error());
            }

            try
            {
                munmap(address, length);
            }
            catch (Exception)
            {
                // The mapping answered the question; leaking it costs one page.
            }

            return name + ": ok";
        }

        private static string ReadProbe(string path)
        {
            int fd = open(path, ORdonly);
            if (fd < 0)
            {
                return Errno(Marshal.GetLastWin32Error());
            }

            close(fd);
            return "ok";
        }

        /// <summary>
        /// The Smack labels on a file, first entry always <c>security.SMACK64</c> so
        /// a caller wanting one line can take it.
        /// </summary>
        private static IList<string> Xattrs(string path)
        {
            var found = new List<string>();
            foreach (string label in Labels)
            {
                string value = Xattr(path, label);
                if (value != null)
                {
                    found.Add(label.Substring("security.".Length) + "=" + value);
                }
            }

            if (found.Count == 0)
            {
                found.Add("SMACK64=(none readable)");
            }

            return found;
        }

        private static string Xattr(string path, string name)
        {
            try
            {
                var buffer = new byte[256];
                IntPtr length = getxattr(path, name, buffer, (IntPtr)buffer.Length);
                long size = length.ToInt64();
                if (size <= 0)
                {
                    int errno = Marshal.GetLastWin32Error();

                    // "This file has no such label" is the ordinary case for three
                    // of the four, and not worth a line each.
                    return errno == 61 /* ENODATA */ || errno == 0 ? null : Errno(errno);
                }

                return Encoding.ASCII.GetString(buffer, 0, (int)size).TrimEnd('\0');
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

        /// <summary>
        /// The mount a path sits on, with its options — which is where a
        /// <c>noexec</c> would be written down.
        ///
        /// The longest matching mount point wins, the way the kernel resolves it.
        /// </summary>
        private static string MountOf(string path)
        {
            try
            {
                string best = null;
                int bestLength = -1;

                foreach (string line in File.ReadAllLines("/proc/self/mountinfo"))
                {
                    // 36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw
                    string[] fields = line.Split(' ');
                    if (fields.Length < 7)
                    {
                        continue;
                    }

                    string point = fields[4];
                    if (!path.StartsWith(point, StringComparison.Ordinal) || point.Length <= bestLength)
                    {
                        continue;
                    }

                    bestLength = point.Length;

                    int separator = Array.IndexOf(fields, "-");
                    string type = separator > 0 && separator + 1 < fields.Length
                        ? fields[separator + 1]
                        : "?";
                    string superOptions = separator > 0 && separator + 3 < fields.Length
                        ? " " + fields[separator + 3]
                        : string.Empty;

                    best = point + " (" + type + " " + fields[5] + superOptions + ")";
                }

                return best ?? "(no mount matched)";
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

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

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Size(string path)
        {
            try
            {
                return new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture) + " bytes";
            }
            catch (Exception ex)
            {
                return "cannot stat: " + ex.GetType().Name;
            }
        }

        private static string FirstLine(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                int end = text.IndexOfAny(new[] { '\r', '\n', '\0' });
                return (end >= 0 ? text.Substring(0, end) : text).Trim();
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

        private static string Field(string path, string prefix)
        {
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line.Substring(prefix.Length).Trim();
                    }
                }

                return "(no " + prefix + " line)";
            }
            catch (Exception ex)
            {
                return "(" + ex.GetType().Name + ")";
            }
        }

        private static string Errno(int errno)
        {
            switch (errno)
            {
                case Eperm:
                    return "EPERM (operation not permitted)";
                case Enoent:
                    return "ENOENT (no such file)";
                case Eacces:
                    return "EACCES (permission denied)";
                default:
                    return "errno " + errno.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Records a finding — and puts it on disk before returning.
        ///
        /// The batch-at-the-end version of this cost a whole round trip with two
        /// users: the report had three lines and stopped, which said only "somewhere
        /// in here". A line that is already written cannot be lost to a signal.
        /// </summary>
        private static void Add(List<string> added, string line)
        {
            lock (Gate)
            {
                Lines.Add(line);
            }

            added.Add(line);
            Breadcrumbs.Drop("  smack: " + line);
        }

        /// <summary>
        /// Names the call that is about to be made, on disk, without putting it in
        /// the report. If the next launch's trail ends on one of these, that call is
        /// the one that does not survive this firmware.
        /// </summary>
        private static void Trace(string step)
        {
            Breadcrumbs.Drop("  " + step);
        }

        /// <summary>The whole finding as one block, for the diagnostics page.</summary>
        public static string Dump()
        {
            string[] snapshot;
            lock (Gate)
            {
                if (Lines.Count == 0)
                {
                    return "  (nothing was blocked)\n";
                }

                snapshot = Lines.ToArray();
            }

            var sb = new StringBuilder();
            foreach (string line in snapshot)
            {
                sb.Append("  ").Append(line).Append('\n');
            }

            return sb.ToString();
        }
    }
}
