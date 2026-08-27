using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Tizen.Applications;

namespace Overscan
{
    /// <summary>
    /// Whether this app may map native code of its own executable — anywhere it can
    /// put a file, and in any form of the request.
    ///
    /// `build-9d856d1` asked this once, about one file in `res/`, and the Q80 in
    /// issue #17 answered: `mmap PROT_READ|PROT_EXEC: EPERM`, `dlopen: failed to map
    /// segment from shared object`, on a library whose `e_machine` and `e_flags`
    /// match the engine's own exactly. So it is not an ABI mismatch, and the stub
    /// idea is refused where it was tried.
    ///
    /// Three things about that measurement are worth one more build before calling
    /// it, and this class asks all three at once because there is no fourth idea
    /// behind them:
    ///
    /// * <b>The directory.</b> The one file of ours that <i>did</i> map executable
    ///   on that set was the app's own assembly, in `bin/`. Ours was in `res/`. Two
    ///   differences at once — the directory and the file format — and only the
    ///   format was named. So the same object now ships in `bin/` too, beside that
    ///   assembly, and is asked there.
    /// * <b>The path form.</b> The assembly answered `yes` at
    ///   `/proc/self/fd/&lt;n&gt;/bin/...`, which is how a .NET launchpad hands over
    ///   its directory; ours was asked by its ordinary `/opt/usr/apps/...` path. The
    ///   same file in `bin/` is asked both ways, which is the only way to separate
    ///   the two. `lib/` — the third package directory, and the conventional home
    ///   for a native library — is asked as well, so no directory in the package is
    ///   left to wonder about.
    /// * <b>The mount.</b> The SFD hook that fits this refusal only inspects unsigned
    ///   ELF on a <i>writable</i> mount, and everywhere we can put a file is
    ///   writable. There is no read-only mount we can write to, so the honest version
    ///   of that test is the app's own writable data directory: a copy, `chmod 0755`,
    ///   asked in the same three ways.
    ///
    /// One control runs in front of all of it: a page of anonymous memory mapped
    /// `PROT_EXEC`. The runtime's own JIT does that on every launch, so it is known
    /// safe, and it separates "this kernel refuses executable memory" from "this
    /// kernel refuses <i>files we supply</i>". Only the second has a shim behind it,
    /// and anonymous memory is no use to a shim: the dynamic loader resolves
    /// `DT_NEEDED` against its own link map, which nothing but `dlopen` writes to.
    ///
    /// If every location refuses, that is the end of the idea rather than the end of
    /// one attempt at it — see *What is left on the Q80* in `docs/INTERNALS.md`.
    ///
    /// Only the tizen5 package ships the library, because only the Q80 needs the
    /// answer and the object is built for one architecture. Everywhere else this
    /// says so and stops.
    /// </summary>
    internal static class NativeProbe
    {
        /// <summary>The library, as it is named in the package.</summary>
        private const string ProbeLibrary = "libovprobe.so";

        /// <summary>A symbol it exports, to prove the handle is real.</summary>
        private const string ProbeSymbol = "ov_probe_marker";

        /// <summary>The engine's own library, for the ABI comparison.</summary>
        private const string EngineLibrary = "/usr/lib/libchromium-ewk.so";

        private const int ORdonly = 0;
        private const int ProtRead = 0x1;
        private const int ProtWrite = 0x2;
        private const int ProtExec = 0x4;
        private const int MapPrivate = 0x02;
        private const int MapAnonymous = 0x20;
        private const int RtldLazy = 0x00001;
        private const int RtldGlobal = 0x00100;

        /// <summary>`rwxr-xr-x`, for the copy in the writable directory.</summary>
        private const uint ExecutableMode = 0x1ED;

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
        private static extern int chmod(string path, uint mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        /// <summary>The one line for the report header.</summary>
        public static string Summary = "(not asked)";

        private static readonly List<string> Lines = new List<string>();

        /// <summary>Guards <see cref="Lines"/>: the report is served from another thread.</summary>
        private static readonly object Gate = new object();

        /// <summary>Where a copy of the library was asked about, and what it said.</summary>
        private sealed class Location
        {
            public Location(string name, string path)
            {
                Name = name;
                Path = path;
            }

            /// <summary>How it is named in the report — `res/`, `bin/`, `data/`.</summary>
            public string Name;

            /// <summary>The file asked about, or null if this location has none.</summary>
            public string Path;

            /// <summary>True once a page of it has mapped `PROT_READ|PROT_EXEC`.</summary>
            public bool MapsExecutable;

            /// <summary>True once the dynamic loader has taken it.</summary>
            public bool Loads;
        }

        /// <summary>
        /// Runs the control, then every location in turn, dropping each reading on
        /// the trail before the call that produces it.
        ///
        /// Every line is written before its call, for the same reason the rest of
        /// this app does it: if one of these is what kills the process, the trail has
        /// to name which. That risk is real here — asking a kernel with an
        /// executable-mapping policy to map a page executable is precisely what such
        /// a policy exists to refuse, and refusing it with a signal is a legal way to
        /// do that.
        ///
        /// Labels and mounts are read for every location at the very end rather than
        /// beside each one. They only ever explain <i>why</i>, and `getxattr` is the
        /// call this set has not come back from twice — so nothing that decides the
        /// question is queued behind it.
        /// </summary>
        public static void Run()
        {
            try
            {
                Trace("native probe: anonymous PROT_EXEC control");
                string anonymous = MapAnonymousExecutable();
                Trace("  anonymous exec memory: " + anonymous);

                var locations = Locate();
                if (locations.Count == 0)
                {
                    Summary = "not shipped in this package";
                    Trace("native probe: " + Summary);
                    return;
                }

                Trace("  engine : " + HeaderOf(EngineLibrary));

                foreach (Location location in locations)
                {
                    Ask(location);
                }

                Summary = Verdict(locations, anonymous);
                Trace("native probe verdict: " + Summary);

                // Last, and only for the record: which mount each copy sat on and
                // what Smack wrote on it. See the note above about ordering.
                foreach (Location location in locations)
                {
                    Trace("  probe: mount of " + location.Name);
                    Trace("  " + location.Name + " mount: " + SmackWall.MountOf(location.Path));

                    Trace("  probe: getxattr on " + location.Name);
                    Trace("  " + location.Name + " labels: " + LabelsOf(location.Path));
                }
            }
            catch (Exception ex)
            {
                Summary = "could not be asked: " + ex.GetType().Name + ": " + ex.Message;
                Trace("  " + Summary);
            }
        }

        /// <summary>Every line produced so far, for the diagnostics report.</summary>
        public static string Dump()
        {
            lock (Gate)
            {
                return Lines.Count == 0
                    ? "  (not probed)\n"
                    : string.Join("\n", Lines.ToArray()) + "\n";
            }
        }

        /// <summary>
        /// The three readings, for one copy of the library: it can be read, a page of
        /// it can be mapped executable, and the dynamic loader will take it. They fail
        /// for unrelated reasons, so they are asked separately.
        ///
        /// The executable mapping is asked twice when it is refused — once by the
        /// file's ordinary path and once through <c>/proc/self/fd</c>. That second
        /// form is the only difference between this measurement and the one on the
        /// app's own assembly that came back `yes`, and a policy keyed on the path
        /// rather than the inode is the one way it could matter.
        /// </summary>
        private static void Ask(Location location)
        {
            int fd = -1;
            try
            {
                Trace("  probe: open " + location.Name + " " + location.Path);
                fd = open(location.Path, ORdonly);
                if (fd < 0)
                {
                    Trace("  " + location.Name + ": cannot even read it — " +
                          Errno(Marshal.GetLastWin32Error()));
                    return;
                }

                // e_flags carries ARM's float-ABI bits, and the engine's own library
                // is the only statement of what this firmware expects. A dlopen
                // refused for an ABI mismatch and one refused by policy read the
                // same from here unless those two numbers are side by side.
                Trace("  probe: read header of " + location.Name);
                Trace("  " + location.Name + " ours  : " + Header(fd));

                Trace("  probe: mmap PROT_READ " + location.Name);
                Trace("  " + location.Name + " mmap " + Map(fd, ProtRead, "PROT_READ"));

                Trace("  probe: mmap PROT_READ|PROT_EXEC " + location.Name);
                string executable = Map(fd, ProtRead | ProtExec, "PROT_READ|PROT_EXEC");
                Trace("  " + location.Name + " mmap " + executable);
                location.MapsExecutable = Succeeded(executable);

                if (!location.MapsExecutable)
                {
                    Trace("  probe: mmap PROT_READ|PROT_EXEC via /proc/self/fd " + location.Name);
                    string reopened = MapThroughProcFd(fd);
                    Trace("  " + location.Name + " mmap via /proc/self/fd: " + reopened);
                    location.MapsExecutable = Succeeded(reopened);
                }

                Trace("  probe: dlopen " + location.Name);
                string loaded = Load(location.Path);
                Trace("  " + location.Name + " dlopen: " + loaded);
                location.Loads = loaded.IndexOf("resolved", StringComparison.Ordinal) >= 0;
            }
            catch (Exception ex)
            {
                Trace("  " + location.Name + " threw " + ex.GetType().Name + ": " + ex.Message);
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
                        // Nothing here is worth failing a diagnostic over.
                    }
                }
            }
        }

        /// <summary>
        /// The verdict line, which is the whole point of the build: whether there is
        /// any location left with a shim behind it.
        /// </summary>
        private static string Verdict(IList<Location> locations, string anonymous)
        {
            var refused = new List<string>();
            foreach (Location location in locations)
            {
                if (location.Loads)
                {
                    return location.Name + " maps executable and dlopen loaded it";
                }

                if (location.MapsExecutable)
                {
                    return location.Name + " maps executable, but dlopen refused it";
                }

                refused.Add(location.Name);
            }

            return "REFUSED in " + string.Join(", ", refused.ToArray()) +
                   " — anonymous exec memory " +
                   (Succeeded(anonymous) ? "is allowed, so this is about files we ship" : anonymous);
        }

        /// <summary>
        /// Every copy of the library this package has, in the order they are worth
        /// asking about.
        ///
        /// All three package directories a tpk has, then a copy on the one mount we
        /// choose. `res/` goes first because it is the one this set has already
        /// survived being asked about; `bin/` matters most, because the app's own
        /// assembly is in it and that is the only file of ours known to map
        /// executable here.
        ///
        /// `bin/` is asked twice, and the second one is the point rather than a
        /// duplicate. TizenFX does not expose that directory, so the only handle on
        /// it from managed code is the assembly's own location — and on Tizen that
        /// arrives as <c>/proc/self/fd/&lt;n&gt;/bin/...</c>, which is exactly the
        /// path form the successful `own code` reading used. Asking by the ordinary
        /// <c>/opt/usr/apps/...</c> path as well is the only way a `yes` can be
        /// attributed to the directory rather than to the path form.
        /// </summary>
        private static IList<Location> Locate()
        {
            var found = new List<Location>();

            // `res/` from the directory TizenFX reports, not from the derived root:
            // it is the baseline reading and must not depend on that derivation.
            Add(found, "res/", Combine(ResourceDirectory(), ProbeLibrary));

            string root = AppRoot();
            string ordinaryBin = Combine(root == null ? null : Path.Combine(root, "bin"), ProbeLibrary);
            Add(found, "bin/", ordinaryBin);

            // The same file again, by the only path managed code can name it with.
            // Skipped when the runtime hands back the ordinary path anyway, which is
            // what happens off-device and on the emulator.
            string assemblyBin = Combine(AssemblyDirectory(), ProbeLibrary);
            if (assemblyBin != null && !string.Equals(assemblyBin, ordinaryBin, StringComparison.Ordinal))
            {
                found.Add(new Location("bin/ (assembly path)", assemblyBin));
            }

            Add(found, "lib/", Combine(root == null ? null : Path.Combine(root, "lib"), ProbeLibrary));

            if (found.Count == 0)
            {
                // Nothing to copy, so nothing to ask about the writable mount either.
                return found;
            }

            string copied = CopyToData(found[0].Path);
            if (copied != null)
            {
                found.Add(new Location("data/", copied));
            }

            return found;
        }

        private static void Add(IList<Location> found, string name, string path)
        {
            if (path != null)
            {
                found.Add(new Location(name, path));
            }
        }

        /// <summary>
        /// The installed package directory, which is the parent of `res/` — the one
        /// directory TizenFX does name, and the only way to reach `bin/` and `lib/`
        /// by their ordinary paths rather than through the runtime's own handle.
        /// </summary>
        private static string AppRoot()
        {
            try
            {
                string resource = ResourceDirectory();
                if (string.IsNullOrEmpty(resource))
                {
                    return null;
                }

                // TizenFX returns it with a trailing slash, which GetDirectoryName
                // would otherwise read as "the res directory itself".
                return Path.GetDirectoryName(resource.TrimEnd('/'));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The library, copied into the app's own writable directory and made
        /// executable. This is the one location whose mount we choose, and the SFD
        /// hook that fits issue #17's refusal is documented as inspecting unsigned
        /// ELF on writable mounts — so a copy here failing the same way is what
        /// closes that reading rather than leaving it open.
        /// </summary>
        private static string CopyToData(string source)
        {
            try
            {
                string data = DataDirectory();
                if (string.IsNullOrEmpty(data))
                {
                    return null;
                }

                string target = Path.Combine(data, ProbeLibrary);
                Trace("  probe: copy " + ProbeLibrary + " to data/");
                File.Copy(source, target, true);

                // Not needed to map a file executable, but a mode a real library
                // would carry, so a refusal cannot be read as being about the bits.
                chmod(target, ExecutableMode);
                return target;
            }
            catch (Exception ex)
            {
                Trace("  probe: cannot copy to data/ — " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A page of anonymous memory, mapped executable. The runtime's own JIT does
        /// this on every launch, so it is safe to ask and it is the control: if even
        /// this is refused, the refusal is about executable memory and not about us.
        /// </summary>
        private static string MapAnonymousExecutable()
        {
            IntPtr length = (IntPtr)4096;
            IntPtr address = mmap(IntPtr.Zero, length, ProtRead | ProtWrite | ProtExec,
                                  MapPrivate | MapAnonymous, -1, IntPtr.Zero);
            if (address == IntPtr.Zero || address.ToInt64() == -1)
            {
                return "refused — " + Errno(Marshal.GetLastWin32Error());
            }

            try
            {
                munmap(address, length);
            }
            catch (Exception)
            {
                // The mapping answered the question; leaking it costs one page.
            }

            return "ok";
        }

        /// <summary>
        /// The same file, mapped executable through a second descriptor opened on
        /// <c>/proc/self/fd/&lt;n&gt;</c>. Same inode and same mount, so a kernel
        /// deciding on either of those answers identically — which is exactly what
        /// makes it worth one line: if this succeeds where the ordinary path failed,
        /// the policy is keyed on the path, and that is a thing a package can change.
        /// </summary>
        private static string MapThroughProcFd(int fd)
        {
            int second = -1;
            try
            {
                string path = "/proc/self/fd/" + fd.ToString(CultureInfo.InvariantCulture);
                second = open(path, ORdonly);
                if (second < 0)
                {
                    return "cannot reopen — " + Errno(Marshal.GetLastWin32Error());
                }

                return Map(second, ProtRead | ProtExec, "PROT_READ|PROT_EXEC");
            }
            catch (Exception ex)
            {
                return "threw " + ex.GetType().Name;
            }
            finally
            {
                if (second >= 0)
                {
                    try
                    {
                        close(second);
                    }
                    catch (Exception)
                    {
                        // Best-effort, like everything else in here.
                    }
                }
            }
        }

        /// <summary>
        /// <c>res/</c> as the installer laid it out — the app's own read-only
        /// directory, and where a real stub would have to live.
        /// </summary>
        private static string ResourceDirectory()
        {
            var info = Directories();
            return info == null ? null : info.Resource;
        }

        /// <summary>The app's writable directory, and the one mount we choose.</summary>
        private static string DataDirectory()
        {
            var info = Directories();
            return info == null ? null : info.Data;
        }

        private static Tizen.Applications.DirectoryInfo Directories()
        {
            try
            {
                return Application.Current == null ? null : Application.Current.DirectoryInfo;
            }
            catch (Exception)
            {
                // Asked before the application object exists. Nothing to probe.
                return null;
            }
        }

        private static string AssemblyDirectory()
        {
            try
            {
                string self = typeof(NativeProbe).GetTypeInfo().Assembly.Location;
                return string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(self);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Combine(string directory, string name)
        {
            try
            {
                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                string path = Path.Combine(directory, name);
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The Smack labels on one of our own copies. `SMACK64MMAP` is the one that
        /// would carry an executable-mapping rule, and reading it on a file of ours
        /// is safe in a way that reading it on the platform's is not.
        /// </summary>
        private static string LabelsOf(string path)
        {
            var found = new List<string>();
            string[] labels = { "security.SMACK64", "security.SMACK64EXEC", "security.SMACK64MMAP" };

            foreach (string label in labels)
            {
                string value = SmackWall.Xattr(path, label);
                if (value != null)
                {
                    found.Add(label.Substring("security.".Length) + "=" + value);
                }
            }

            return found.Count == 0 ? "(none readable)" : string.Join(" ", found.ToArray());
        }

        /// <summary>
        /// <c>e_machine</c> and <c>e_flags</c> off an open ELF, or why not. Fifty-two
        /// bytes is the whole ELF32 header; e_flags sits at offset 36 and is the
        /// field the float ABI is written in.
        /// </summary>
        private static string Header(int fd)
        {
            var head = new byte[52];
            IntPtr got = read(fd, head, (IntPtr)head.Length);
            if (got.ToInt64() < head.Length)
            {
                return "short read (" + got.ToInt64() + " bytes)";
            }

            if (head[0] != 0x7F || head[1] != (byte)'E' || head[2] != (byte)'L' || head[3] != (byte)'F')
            {
                return "not an ELF";
            }

            int machine = head[18] | (head[19] << 8);
            long flags = (long)head[36] | ((long)head[37] << 8) | ((long)head[38] << 16) | ((long)head[39] << 24);

            return "e_machine=" + machine.ToString(CultureInfo.InvariantCulture) +
                   " e_flags=0x" + flags.ToString("x", CultureInfo.InvariantCulture) +
                   " float=" + FloatAbi(flags);
        }

        private static string HeaderOf(string path)
        {
            int fd = open(path, ORdonly);
            if (fd < 0)
            {
                return path + ": " + Errno(Marshal.GetLastWin32Error());
            }

            try
            {
                return Header(fd);
            }
            finally
            {
                try
                {
                    close(fd);
                }
                catch (Exception)
                {
                    // Best-effort, like everything else in here.
                }
            }
        }

        /// <summary>
        /// The two ARM EABI float-ABI bits, named. They decide whether the loader
        /// will look at a library at all, and they are the one way this test can
        /// fail for a reason that has nothing to do with permission.
        /// </summary>
        private static string FloatAbi(long flags)
        {
            bool soft = (flags & 0x00000200) != 0;
            bool hard = (flags & 0x00000400) != 0;

            if (soft)
            {
                return "soft";
            }

            return hard ? "hard" : "unspecified";
        }

        private static string Map(int fd, int protection, string name)
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

        private static bool Succeeded(string reading)
        {
            return reading != null && reading.EndsWith("ok", StringComparison.Ordinal);
        }

        /// <summary>
        /// The loader's own verdict. <c>RTLD_GLOBAL</c> because that is how a stub
        /// would have to be loaded for the engine's own <c>DT_NEEDED</c> to resolve
        /// against it, and <c>RTLD_LAZY</c> because a stub only ever resolves the
        /// symbols that are actually called.
        /// </summary>
        private static string Load(string path)
        {
            dlerror();

            IntPtr handle = dlopen(path, RtldLazy | RtldGlobal);
            if (handle == IntPtr.Zero)
            {
                return "refused — " + LastDlError();
            }

            IntPtr symbol = dlsym(handle, ProbeSymbol);
            return symbol == IntPtr.Zero
                ? "loaded, but " + ProbeSymbol + " did not resolve — " + LastDlError()
                : "loaded, " + ProbeSymbol + " resolved";
        }

        private static string LastDlError()
        {
            IntPtr message = dlerror();
            return message == IntPtr.Zero
                ? "(no message)"
                : Marshal.PtrToStringAnsi(message);
        }

        /// <summary>
        /// Onto the trail and into the report, in that order — the trail is the half
        /// that survives a process this probe might kill.
        /// </summary>
        private static void Trace(string line)
        {
            Breadcrumbs.Drop(line);

            lock (Gate)
            {
                Lines.Add(line);
            }
        }

        private static string Errno(int errno)
        {
            switch (errno)
            {
                case 1:
                    return "EPERM (operation not permitted)";
                case 2:
                    return "ENOENT (no such file)";
                case 13:
                    return "EACCES (permission denied)";
                default:
                    return "errno " + errno.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
