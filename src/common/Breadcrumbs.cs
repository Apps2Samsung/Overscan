using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

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
    ///
    /// <b>The writing is done by a thread of its own, and nobody waits on it for
    /// more than two seconds.</b> Until `build-1a5fd68` every line was written on
    /// the thread that dropped it, which made the trail a promise it could not keep
    /// on issue #17's set: that set's report of 2026-09-05 has a launch on which the
    /// probe thread, the watchdog thread behind it and the heartbeat thread beside
    /// it all went silent at the same moment — three threads with nothing in common
    /// but the file they report through — and another launch, alive enough to serve
    /// the diagnostics page, whose trail had stopped a second in. Every stall that
    /// set has ever shown was on a call immediately followed by a trail write, and
    /// a trail written on the caller's thread cannot say which of the two stalled;
    /// a watchdog and a heartbeat that report through the same write cannot say
    /// anything at all. So the write happens over there, the caller waits for it
    /// only as long as a write should take — which keeps "the last line is the call
    /// that killed us" true whenever the disk is behaving — and when it does not
    /// come back, the caller carries on, the line stays in <see cref="DiagLog"/>'s
    /// memory for the page, and <see cref="Status"/> says so in the report's header:
    /// which write, how long, how many lines behind it. A trail that is stuck is
    /// finally a finding rather than a silence.
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
                if (InitIn(dir))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Starts the trail in <paramref name="dir"/>, or reports that it is
        /// not writable. Public for <c>tools/trail</c>, which points it at a
        /// directory of its own and then does to the file what issue #17's set does
        /// to ours.
        /// </summary>
        public static bool InitIn(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return false;
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

                return true;
            }
            catch (Exception)
            {
                // Not writable; the caller tries the next candidate.
                return false;
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
            DiagLog.Record(message);
            Write(message, true, true);
        }

        /// <summary>
        /// A trail line that does not go to the on-screen log. <see cref="DiagLog"/>
        /// keeps 60 lines, so anything that ticks — see <see cref="Heartbeat"/> —
        /// would evict the start-up lines that say where the app got to. The trail
        /// file has no such limit and is the thing read after a crash anyway.
        /// </summary>
        public static void DropToTrail(string message)
        {
            Write(message, false, true);
        }

        /// <summary>
        /// dlog only, for <see cref="DiagLog.Add"/>: a line the on-screen log has
        /// already kept and the trail file does not want. It goes through the writer
        /// like everything else, because a dlog call is a write to a socket some
        /// other process may or may not be reading.
        /// </summary>
        public static void Log(string message)
        {
            Write(message, true, false);
        }

        /// <summary>
        /// How the writing is going, for the report's header. "N lines on disk" is
        /// the good answer. The other one names the write the writer is inside, how
        /// long it has been there and how many lines are queued behind it — all of
        /// which are in <see cref="DiagLog"/> and therefore on the page carrying this
        /// line, which is the point of not waiting for them.
        /// </summary>
        public static string Status
        {
            get
            {
                lock (WriterGate)
                {
                    if (_writerFailed)
                    {
                        return "written inline — this set would not give the trail a thread";
                    }

                    string done = _linesOnDisk.ToString() + " lines on disk";

                    if (_stalledSince.HasValue && _inFlight != null)
                    {
                        int held = (int)(DateTime.Now - _inFlightSince).TotalSeconds;
                        return "STALLED — the " + (_stage ?? "write") + " has held \"" + Brief(_inFlight.Text) +
                               "\" for " + held + " s; " + Pending.Count +
                               " line(s) queued behind it are in \"this run\" below and nowhere else";
                    }

                    if (_stalls > 0)
                    {
                        done += "; the writer stalled " + _stalls + " time(s), longest " +
                                (_longestStallMs / 1000) + " s in the " + (_longestStage ?? "write") +
                                ", and caught up";
                    }

                    return done;
                }
            }
        }

        /// <summary>How long a caller waits for its line before carrying on without it.</summary>
        private const int WaitMs = 2000;

        private sealed class Line
        {
            public long Seq;
            public string Stamp;
            public string Text;
            public bool ToLog;
            public bool ToTrail;
        }

        private static readonly object WriterGate = new object();
        private static readonly Queue<Line> Pending = new Queue<Line>();
        private static Thread _writer;
        private static bool _writerFailed;
        private static long _queued;
        private static long _committed;
        private static long _linesOnDisk;
        private static Line _inFlight;
        private static DateTime _inFlightSince;
        private static string _stage;
        private static DateTime? _stalledSince;
        private static int _stalls;
        private static long _longestStallMs;
        private static string _longestStage;

        /// <summary>
        /// Hands a line to the writer and waits for it to land — normally a
        /// millisecond, so a crash on the very next call still finds this line on
        /// disk — and gives up after <see cref="WaitMs"/>. Once a wait has given up,
        /// nobody waits again until the writer has caught up: a stuck writer must
        /// not cost every later line two seconds, and the lines are in memory anyway.
        ///
        /// The timestamp is taken here, not in the writer, so a line that waited
        /// behind a stall still carries the time of the thing it describes.
        /// </summary>
        private static void Write(string text, bool toLog, bool toTrail)
        {
            var line = new Line
            {
                Stamp = DateTime.Now.ToString("HH:mm:ss"),
                Text = text,
                ToLog = toLog,
                ToTrail = toTrail,
            };

            bool inline;
            lock (WriterGate)
            {
                EnsureWriter();
                inline = _writerFailed;
                if (!inline)
                {
                    line.Seq = ++_queued;
                    Pending.Enqueue(line);
                    Monitor.PulseAll(WriterGate);
                }
            }

            if (inline)
            {
                // The dangerous way, which is what every build before this one did
                // on every line.
                Perform(line);
                return;
            }

            lock (WriterGate)
            {
                if (_stalledSince.HasValue)
                {
                    return;
                }

                DateTime until = DateTime.Now.AddMilliseconds(WaitMs);
                while (_committed < line.Seq)
                {
                    int remaining = (int)(until - DateTime.Now).TotalMilliseconds;
                    if (remaining <= 0)
                    {
                        _stalledSince = DateTime.Now;
                        _stalls++;
                        return;
                    }

                    Monitor.Wait(WriterGate, remaining);
                }
            }
        }

        /// <summary>Starts the writer the first time a line needs it. Under <see cref="WriterGate"/>.</summary>
        private static void EnsureWriter()
        {
            if (_writer != null || _writerFailed)
            {
                return;
            }

            try
            {
                var thread = new Thread(WriteForever);
                thread.IsBackground = true;
                thread.Name = "trail";
                thread.Start();
                _writer = thread;
            }
            catch (Exception)
            {
                _writerFailed = true;
            }
        }

        private static void WriteForever()
        {
            while (true)
            {
                Line line;
                lock (WriterGate)
                {
                    while (Pending.Count == 0)
                    {
                        Monitor.Wait(WriterGate);
                    }

                    line = Pending.Dequeue();
                    _inFlight = line;
                    _inFlightSince = DateTime.Now;
                }

                string slowest = Perform(line);

                lock (WriterGate)
                {
                    _committed = line.Seq;
                    if (line.ToTrail && _path != null)
                    {
                        _linesOnDisk++;
                    }

                    if (_stalledSince.HasValue)
                    {
                        long stalledFor = (long)(DateTime.Now - _inFlightSince).TotalMilliseconds;
                        if (stalledFor > _longestStallMs)
                        {
                            _longestStallMs = stalledFor;
                            _longestStage = slowest;
                        }

                        _stalledSince = null;
                    }

                    _inFlight = null;
                    _stage = null;
                    Monitor.PulseAll(WriterGate);
                }
            }
        }

        /// <summary>
        /// The two writes, each named before it is made so <see cref="Status"/> can
        /// say which one the writer is inside. Returns the one that took longest, for
        /// the record of a stall that has since cleared.
        /// </summary>
        private static string Perform(Line line)
        {
            string slowest = null;
            long slowestMs = -1;

            if (line.ToLog)
            {
                _stage = "dlog";
                long started = Environment.TickCount;
                try
                {
                    Tizen.Log.Info(DiagLog.LogTag, line.Text);
                }
                catch (Exception)
                {
                    // Never let logging be the thing that breaks the run.
                }

                slowestMs = Environment.TickCount - started;
                slowest = "dlog";
            }

            if (line.ToTrail && _path != null)
            {
                _stage = "trail file";
                long started = Environment.TickCount;
                try
                {
                    File.AppendAllText(_path, line.Stamp + "  " + line.Text + "\n");
                }
                catch (Exception)
                {
                    // Never let logging be the thing that breaks the run.
                }

                if (Environment.TickCount - started > slowestMs)
                {
                    slowest = "trail file";
                }
            }

            return slowest ?? "write";
        }

        private static string Brief(string text)
        {
            return text == null ? "" : text.Length <= 60 ? text : text.Substring(0, 57) + "...";
        }
    }
}
