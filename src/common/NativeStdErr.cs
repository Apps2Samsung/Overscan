using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Overscan
{
    /// <summary>
    /// Catches what a native call writes to stdout/stderr and hands it back as a
    /// string.
    ///
    /// Every path in <c>ewk_init()</c> that returns 0 logs its reason first —
    /// <c>ERR("could not init ecore_imf.")</c> and friends — through EINA_LOG,
    /// which writes to stderr. On a desktop that lands in the terminal; on a
    /// retail TV it goes to dlog, and a set with intershell disabled has no
    /// <c>dlogutil</c> to read it with. So the app reads its own stderr instead,
    /// by pointing file descriptors 1 and 2 at a file for the duration of the call.
    ///
    /// A file, not a pipe: a pipe's 64 KiB buffer would deadlock the process if the
    /// engine ever logged more than that while nobody was draining it, and a
    /// successful chromium start-up is chatty. A file cannot block.
    ///
    /// Keep the captured call short, and keep it on this side of anything that
    /// forks. A child process inherits the redirected descriptors and would go on
    /// writing to the file for the rest of its life, long after we have restored
    /// ours. <c>ewk_init</c> is safe on that count — it starts no processes, and
    /// <c>_ewk_init_web_engine()</c> is an empty function; chromium's browser and
    /// render processes are launched later, when the Ewk_Context and the view are
    /// created.
    ///
    /// Best-effort throughout — if any of the plumbing fails the action still runs,
    /// and the return value says so.
    /// </summary>
    internal static class NativeStdErr
    {
        private const int StdOut = 1;
        private const int StdErr = 2;

        /// <summary>Enough for a stack of EINA_LOG lines, short enough to read on a TV.</summary>
        private const int Keep = 1800;

        [DllImport("libc.so.6")]
        private static extern int dup(int fd);

        [DllImport("libc.so.6")]
        private static extern int dup2(int oldfd, int newfd);

        [DllImport("libc.so.6")]
        private static extern int close(int fd);

        /// <summary>NULL flushes every open stream, which is what we want here.</summary>
        [DllImport("libc.so.6")]
        private static extern int fflush(IntPtr stream);

        /// <summary>
        /// Runs <paramref name="action"/> with 1 and 2 redirected, and returns what
        /// it wrote. An exception from <paramref name="action"/> propagates, with
        /// the descriptors restored first.
        /// </summary>
        public static string Capture(Action action)
        {
            string directory = Breadcrumbs.TrailDirectory;
            if (directory == null)
            {
                action();
                return "(not captured: nowhere writable)";
            }

            string path = Path.Combine(directory, "stderr.log");
            FileStream sink = null;
            int savedOut = -1;
            int savedErr = -1;
            bool redirected = false;
            string why = null;

            // The plumbing is set up in its own try so that a DllNotFoundException
            // out of the *action* — which is exactly what Chromium.Initialize can
            // throw on a set without the engine — is never mistaken for a missing
            // libc and the action never runs twice.
            try
            {
                sink = new FileStream(path, FileMode.Create, FileAccess.Write);

                // On .NET Core/Unix a SafeFileHandle *is* the descriptor, which
                // saves P/Invoking open() — a variadic function, and the one call
                // here whose ABI would be worth arguing about.
                int fd = (int)sink.SafeFileHandle.DangerousGetHandle();
                savedOut = dup(StdOut);
                savedErr = dup(StdErr);

                if (fd < 0 || savedOut < 0 || savedErr < 0)
                {
                    why = "(not captured: could not duplicate descriptors)";
                }
                else
                {
                    dup2(fd, StdOut);
                    dup2(fd, StdErr);
                    redirected = true;
                }
            }
            catch (Exception ex)
            {
                why = "(not captured: " + ex.GetType().Name + ")";
            }

            try
            {
                action();
            }
            finally
            {
                if (redirected)
                {
                    // stderr is unbuffered, but redirecting stdout to a file makes
                    // it block-buffered, so anything still in libc's buffer has to
                    // be pushed out before the descriptor goes back.
                    try
                    {
                        fflush(IntPtr.Zero);
                    }
                    catch (Exception)
                    {
                        // Nothing to do about it; the stderr lines are the ones
                        // that matter and those are already on disk.
                    }

                    dup2(savedOut, StdOut);
                    dup2(savedErr, StdErr);
                }

                Safely(savedOut);
                Safely(savedErr);

                if (sink != null)
                {
                    sink.Dispose();
                }
            }

            return redirected ? Read(path) : why;
        }

        // ------------------------------------------------- the whole session

        /// <summary>
        /// Where the session capture stands, for the report.
        /// </summary>
        public static string SessionState { get; private set; } = "(not started)";

        private static FileStream _sessionSink;
        private static int _sessionOut = -1;
        private static int _sessionErr = -1;

        /// <summary>
        /// Redirects stdout and stderr for the rest of the run, rather than for the
        /// length of one call.
        ///
        /// The warning above about forking becomes the reason to do this. A child
        /// process inherits the redirected descriptors and goes on writing to the
        /// file — and on the NUI build the child is chromium's render process, which
        /// is the thing suspected of taking the app down on issue #20. A native
        /// crash prints before it dies: the runtime's own fatal handler, glibc's
        /// assertion text, chromium's <c>Received signal</c> line. All of it goes to
        /// stderr, all of it is invisible on a retail TV with no dlog reader, and
        /// <see cref="Breadcrumbs"/> already moves this file aside at start-up so
        /// the next launch can read what the last one printed.
        ///
        /// The size is watched (<see cref="TrimSession"/>) because an engine that
        /// turns chatty must not be allowed to fill a stranger's TV.
        ///
        /// This shares its file with <see cref="Capture"/>, and the two must not
        /// overlap: <c>Capture</c> opens the same path <c>FileMode.Create</c>, which
        /// would truncate the file underneath the descriptors this holds. They do
        /// not meet today — <c>Capture</c> wraps <c>Chromium.Initialize</c>, which
        /// only the ewk packages and the probe call, and this is only called by the
        /// NUI build — and a package that wants both needs to give one of them its
        /// own file.
        /// </summary>
        public static bool StartSession()
        {
            string directory = Breadcrumbs.TrailDirectory;
            if (directory == null)
            {
                SessionState = "not capturing: nowhere writable";
                return false;
            }

            if (_sessionSink != null)
            {
                return true;
            }

            try
            {
                _sessionSink = new FileStream(Path.Combine(directory, "stderr.log"),
                                              FileMode.Create, FileAccess.Write);

                int fd = (int)_sessionSink.SafeFileHandle.DangerousGetHandle();
                _sessionOut = dup(StdOut);
                _sessionErr = dup(StdErr);

                if (fd < 0 || _sessionOut < 0 || _sessionErr < 0)
                {
                    SessionState = "not capturing: could not duplicate descriptors";
                    return false;
                }

                dup2(fd, StdOut);
                dup2(fd, StdErr);
                SessionState = "capturing to stderr.log";
                return true;
            }
            catch (Exception ex)
            {
                SessionState = "not capturing: " + ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// Puts the descriptors back. Called when the file has grown past anything
        /// worth keeping — the tail is what gets read, but a file nobody bounds is
        /// still a file on somebody's television.
        /// </summary>
        public static void StopSession(string why)
        {
            if (_sessionSink == null)
            {
                return;
            }

            try
            {
                fflush(IntPtr.Zero);
            }
            catch (Exception)
            {
                // The stderr lines are unbuffered and already on disk.
            }

            if (_sessionOut >= 0)
            {
                dup2(_sessionOut, StdOut);
            }

            if (_sessionErr >= 0)
            {
                dup2(_sessionErr, StdErr);
            }

            Safely(_sessionOut);
            Safely(_sessionErr);
            _sessionOut = -1;
            _sessionErr = -1;

            try
            {
                _sessionSink.Dispose();
            }
            catch (Exception)
            {
                // Going away regardless.
            }

            _sessionSink = null;
            SessionState = "stopped: " + why;
            Breadcrumbs.DropToTrail("stderr capture " + SessionState);
        }

        /// <summary>
        /// Stops the capture if the file has grown past <paramref name="limitBytes"/>.
        /// Cheap enough for a heartbeat: one stat of a file we already have open.
        /// </summary>
        public static void TrimSession(long limitBytes)
        {
            if (_sessionSink == null)
            {
                return;
            }

            try
            {
                if (_sessionSink.Length > limitBytes)
                {
                    StopSession("the engine wrote more than " + (limitBytes / 1024) + " KB");
                }
            }
            catch (Exception)
            {
                // A length that cannot be read is not a reason to stop capturing.
            }
        }

        private static void Safely(int fd)
        {
            if (fd < 0)
            {
                return;
            }

            try
            {
                close(fd);
            }
            catch (Exception)
            {
                // A leaked descriptor is not worth failing a diagnostic over.
            }
        }

        private static string Read(string path)
        {
            try
            {
                string text = File.ReadAllText(path).Trim();
                if (text.Length == 0)
                {
                    return "(nothing written to stdout/stderr)";
                }

                return text.Length <= Keep
                    ? text
                    : "…" + text.Substring(text.Length - Keep);
            }
            catch (Exception ex)
            {
                return "(not captured: " + ex.GetType().Name + ")";
            }
        }
    }
}
