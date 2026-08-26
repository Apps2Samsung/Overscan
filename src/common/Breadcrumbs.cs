using System;
using System.IO;

namespace Overscan
{
    /// <summary>
    /// Append-only trail that survives a hard crash.
    ///
    /// On a locked-down TV there is no dlog, no shell, and `sdb pull` refuses the
    /// crash directories — and a native crash (SIGSEGV/SIGILL) kills the process
    /// too fast for <see cref="DiagServer"/> to have bound its socket, so "nothing
    /// answers" tells us nothing about *where* it died. Every line here is written
    /// with a separate open/append/close, so whatever reached disk before the crash
    /// is readable on the NEXT launch: the last line is the call that killed us.
    /// </summary>
    internal static class Breadcrumbs
    {
        private static string _path;
        private static string _previous = "(no previous run recorded)";
        private static string _previousStdErr = "(no previous run recorded)";

        /// <summary>The trail left by the previous launch.</summary>
        public static string Previous
        {
            get { return _previous; }
        }

        /// <summary>
        /// What the previous launch's native calls wrote to stdout/stderr.
        ///
        /// <see cref="NativeStdErr"/> only reads its scratch file back once the call
        /// it wrapped has returned, so a call that never returns leaves its output
        /// on disk and hands back nothing. On issue #17's set that is every run:
        /// the trail's last line is <c>Chromium.Initialize()</c>, so ewk_init does
        /// not return 0 — it does not return. Its EINA_LOG lines are the one thing
        /// that would say why, and they were already there; they were just being
        /// truncated by the next run before anyone read them.
        /// </summary>
        public static string PreviousStdErr
        {
            get { return _previousStdErr; }
        }

        /// <summary>Where the trail ended up, for the report.</summary>
        public static string Location
        {
            get { return _path ?? "(nowhere writable)"; }
        }

        /// <summary>
        /// The directory the trail landed in, or null when none was writable.
        /// <see cref="NativeStdErr"/> needs a scratch file and this is the one
        /// place that has already worked out where the app may write.
        ///
        /// Not called "Directory": that name would shadow System.IO.Directory for
        /// the whole class, and <see cref="Init"/> uses it.
        /// </summary>
        public static string TrailDirectory
        {
            get { return _path == null ? null : Path.GetDirectoryName(_path); }
        }

        /// <summary>
        /// Picks the first writable directory. Application.Current is null this
        /// early in Main, so the app's own data dir cannot be asked for by name and
        /// the known layouts are tried directly.
        /// </summary>
        public static void Init(string packageId)
        {
            string[] candidates =
            {
                "/home/owner/apps_rw/" + packageId + "/data",
                "/opt/usr/home/owner/apps_rw/" + packageId + "/data",
                "/opt/usr/apps/" + packageId + "/data",
                Path.GetTempPath(),
                "/tmp",
            };

            foreach (string dir in candidates)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(dir, "breadcrumbs.log");
                    string previous = Path.Combine(dir, "breadcrumbs.prev.log");

                    // Roll the last run aside so this run starts clean but the
                    // previous trail is still readable.
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            if (File.Exists(previous))
                            {
                                File.Delete(previous);
                            }

                            File.Move(candidate, previous);
                        }
                        catch (Exception)
                        {
                            // Keep going: a stale trail is better than none.
                        }
                    }

                    File.AppendAllText(candidate, "--- run started ---\n");
                    _path = candidate;

                    if (File.Exists(previous))
                    {
                        _previous = File.ReadAllText(previous);
                    }

                    // The same treatment for NativeStdErr's scratch file, and it has
                    // to happen here: that class opens it FileMode.Create, so by the
                    // time it is next used the previous run's output is gone. This is
                    // the only moment between the two runs.
                    RollStdErr(dir);

                    return;
                }
                catch (Exception)
                {
                    // Not writable; try the next candidate.
                }
            }
        }

        /// <summary>
        /// Moves the last run's captured output aside and reads it, so a native call
        /// that never returned still gets to say what it printed. Best-effort: a
        /// missing or unreadable file just means there is nothing to report.
        /// </summary>
        private static void RollStdErr(string dir)
        {
            try
            {
                string current = Path.Combine(dir, "stderr.log");
                string previous = Path.Combine(dir, "stderr.prev.log");

                if (File.Exists(current))
                {
                    if (File.Exists(previous))
                    {
                        File.Delete(previous);
                    }

                    File.Move(current, previous);
                }

                if (File.Exists(previous))
                {
                    string text = File.ReadAllText(previous).Trim();
                    _previousStdErr = text.Length == 0
                        ? "(previous run wrote nothing to stdout/stderr)"
                        : text;
                }
            }
            catch (Exception)
            {
                _previousStdErr = "(previous run's output could not be read)";
            }
        }

        public static void Drop(string message)
        {
            DiagLog.Add(message);
            DropToTrail(message);
        }

        /// <summary>
        /// A trail line that does not go to the on-screen log. <see cref="DiagLog"/>
        /// keeps 60 lines, so anything that ticks — see <see cref="Heartbeat"/> —
        /// would evict the start-up lines that say where the app got to. The trail
        /// file has no such limit and is the thing read after a crash anyway.
        /// </summary>
        public static void DropToTrail(string message)
        {
            if (_path == null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss") + "  " + message + "\n");
            }
            catch (Exception)
            {
                // Never let logging be the thing that breaks the run.
            }
        }
    }
}
