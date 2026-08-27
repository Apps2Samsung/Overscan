using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Tizen.Applications;

namespace Overscan
{
    /// <summary>
    /// Whether this app may load a native library of its own on this set.
    ///
    /// Everything issue #17 has measured so far was measured on the app's own
    /// assembly — and that is a **PE** file. Samsung's loader hooks only inspect
    /// files they recognise as ELF, so `own code: yes` says nothing at all about
    /// the one thing the remaining idea for that TV depends on. This ships a real
    /// ARM shared object in <c>res/</c> and asks the set about it directly.
    ///
    /// Three readings, in the order they would matter to a library that has to
    /// work: the file can be read, a page of it can be mapped executable, and the
    /// dynamic loader will take it. They fail for unrelated reasons, so they are
    /// asked separately — a refusal to map is a policy, a refusal to load can just
    /// as easily be an ABI the TV does not use.
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
        private const int ProtExec = 0x4;
        private const int MapPrivate = 0x02;
        private const int RtldLazy = 0x00001;
        private const int RtldGlobal = 0x00100;

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

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlsym(IntPtr handle, string symbol);

        /// <summary>The one line for the report header.</summary>
        public static string Summary = "(not asked)";

        /// <summary>
        /// Runs the three readings, dropping each on the trail as it goes.
        ///
        /// Every line is written before the call it describes, for the same reason
        /// the rest of this app does it: if one of these is what kills the process,
        /// the trail has to name which. That risk is real here — asking a kernel
        /// with an executable-mapping policy to map a page executable is precisely
        /// what such a policy exists to refuse, and refusing it with a signal is a
        /// legal way to do that.
        /// </summary>
        public static void Run()
        {
            string path = Locate();
            if (path == null)
            {
                Summary = "not shipped in this package";
                Breadcrumbs.Drop("native probe: " + Summary);
                return;
            }

            Breadcrumbs.Drop("native probe: " + path);

            int fd = -1;
            try
            {
                Breadcrumbs.Drop("  probe: open " + ProbeLibrary);
                fd = open(path, ORdonly);
                if (fd < 0)
                {
                    Summary = "cannot even read it — " + Errno(Marshal.GetLastWin32Error());
                    Breadcrumbs.Drop("  " + Summary);
                    return;
                }

                // e_flags carries ARM's float-ABI bits, and the engine's own library
                // is the only statement of what this firmware expects. A dlopen
                // refused for an ABI mismatch and one refused by policy read the
                // same from here unless these two numbers are side by side.
                Breadcrumbs.Drop("  probe: read header");
                Breadcrumbs.Drop("  ours   : " + Header(fd));
                Breadcrumbs.Drop("  engine : " + HeaderOf(EngineLibrary));

                Breadcrumbs.Drop("  probe: mmap PROT_READ");
                string readable = Map(fd, ProtRead, "PROT_READ");
                Breadcrumbs.Drop("  mmap " + readable);

                Breadcrumbs.Drop("  probe: mmap PROT_READ|PROT_EXEC");
                string executable = Map(fd, ProtRead | ProtExec, "PROT_READ|PROT_EXEC");
                Breadcrumbs.Drop("  mmap " + executable);

                bool mapped = executable.EndsWith("ok", StringComparison.Ordinal);

                Breadcrumbs.Drop("  probe: dlopen " + ProbeLibrary);
                string loaded = Load(path);
                Breadcrumbs.Drop("  dlopen: " + loaded);

                Summary = mapped
                    ? "maps executable; dlopen " + loaded
                    : "REFUSED — " + executable;
            }
            catch (Exception ex)
            {
                Summary = "could not be asked: " + ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("  " + Summary);
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
        /// Where the package put it. <c>res/</c> is the app's own read-only
        /// directory — the one place a sideloaded app has that an app rule grants
        /// `rxl` on, and where a real library of ours would have to live too.
        /// </summary>
        private static string Locate()
        {
            try
            {
                string resource = Application.Current == null
                    ? null
                    : Application.Current.DirectoryInfo.Resource;

                if (string.IsNullOrEmpty(resource))
                {
                    return null;
                }

                string path = Path.Combine(resource, ProbeLibrary);
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                // Asked before the application object exists, or on a package that
                // does not ship the library. Either way there is nothing to probe.
                return null;
            }
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
