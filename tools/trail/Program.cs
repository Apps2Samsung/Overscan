using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Overscan.Harness
{
    /// <summary>
    /// Holds Breadcrumbs to the two things it now promises at once: that a line is on
    /// disk before Drop returns whenever the disk is behaving, and that a disk (or a
    /// dlog) that is not behaving cannot hold the thread that dropped the line, or
    /// hide the fact from the report.
    ///
    /// run.sh lays the files out — a directory, a FIFO in place of the trail file —
    /// and this asserts what the trail did with them.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: trail <scenario> <dir>");
                return 2;
            }

            string scenario = args[0];
            string dir = args[1];
            var clock = Stopwatch.StartNew();

            switch (scenario)
            {
                case "plain":
                    Plain(dir);
                    break;
                case "filehang":
                    FileHang(dir);
                    break;
                case "dloghang":
                    DlogHang(dir);
                    break;
                default:
                    Console.Error.WriteLine("unknown scenario: " + scenario);
                    return 2;
            }

            Console.WriteLine(_failures == 0
                ? "  PASS " + scenario + " (" + clock.ElapsedMilliseconds + " ms)"
                : "  FAIL " + scenario + " — " + _failures + " assertion(s)");

            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// The disk behaves: a dropped line is on it by the time Drop returns, which is
        /// the property "the last line is the call that killed us" rests on.
        /// </summary>
        private static void Plain(string dir)
        {
            Expect(Breadcrumbs.InitIn(dir), "the trail starts in the harness's directory");
            string path = Path.Combine(dir, "breadcrumbs.log");

            var clock = Stopwatch.StartNew();
            Breadcrumbs.Drop("first");
            Breadcrumbs.DropToTrail("second");
            long took = clock.ElapsedMilliseconds;

            string trail = File.ReadAllText(path);
            Expect(trail.Contains("  first\n") && trail.Contains("  second\n"),
                   "both lines are on disk the moment Drop returns");
            Expect(trail.IndexOf("first", StringComparison.Ordinal) < trail.IndexOf("second", StringComparison.Ordinal),
                   "in the order they were dropped");
            Expect(took < 1500, "without waiting anything like the deadline (" + took + " ms)");
            Expect(Breadcrumbs.Status == "2 lines on disk", "and the header counts them (" + Breadcrumbs.Status + ")");
            Expect(Tizen.Log.Calls == 1, "dlog saw the one line meant for it (" + Tizen.Log.Calls + ")");
            Expect(DiagLog.Dump().Contains("first") && !DiagLog.Dump().Contains("second"),
                   "and the on-screen log kept the one meant for it");
        }

        /// <summary>
        /// The trail file is a FIFO with no reader: an open that blocks for as long as
        /// anyone will wait, which is what issue #17's set does with a file it will not
        /// talk about. The dropping thread has to come back, the page has to say so,
        /// and when the disk comes back every line has to land, in order, with the time
        /// it was dropped rather than the time it was written.
        /// </summary>
        private static void FileHang(string dir)
        {
            Expect(Breadcrumbs.InitIn(dir), "the trail starts on a real file");
            string path = Path.Combine(dir, "breadcrumbs.log");

            // One line while the disk behaves, so the writer exists and is warm and
            // what is timed below is the stall and nothing else.
            Breadcrumbs.DropToTrail("warm-up");
            Expect(File.ReadAllText(path).Contains("warm-up"), "a line lands while the disk behaves");

            // Swap the file for a FIFO under the trail's feet, the way run.sh cannot
            // — InitIn has to see a regular file to write its first line to.
            File.Delete(path);
            Expect(Mkfifo(path), "the trail file is now a FIFO nobody is reading");

            var clock = Stopwatch.StartNew();
            Breadcrumbs.Drop("one");
            long first = clock.ElapsedMilliseconds;
            Expect(first >= 1500 && first < 4000,
                   "the first line waits its two seconds and no longer (" + first + " ms)");

            clock.Restart();
            Breadcrumbs.Drop("two");
            Breadcrumbs.DropToTrail("three");
            long rest = clock.ElapsedMilliseconds;
            Expect(rest < 300, "the lines behind a known stall do not wait at all (" + rest + " ms)");

            string status = Breadcrumbs.Status;
            Expect(status.StartsWith("STALLED — the trail file has held \"one\""),
                   "the header names the write and the line it is holding (" + status + ")");
            Expect(status.Contains("2 line(s) queued"), "and how many are behind it (" + status + ")");
            Expect(DiagLog.Dump().Contains("one") && DiagLog.Dump().Contains("two"),
                   "the on-screen log has every line the disk does not");

            // The disk comes back. Each append is its own open/write/close, so a FIFO
            // hands its reader EOF after every line and has to be opened again for the
            // next — three sessions for three lines.
            string drained = "";
            var reader = new Thread(delegate ()
            {
                var sb = new System.Text.StringBuilder();
                int lines = 0;
                var patience = Stopwatch.StartNew();
                while (lines < 3 && patience.ElapsedMilliseconds < 10000)
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    using (var text = new StreamReader(stream))
                    {
                        string line;
                        while ((line = text.ReadLine()) != null)
                        {
                            sb.Append(line).Append('\n');
                            lines++;
                        }
                    }
                }

                drained = sb.ToString();
            });
            reader.IsBackground = true;
            reader.Start();
            Expect(reader.Join(12000), "the writer drains the moment there is a reader");

            Expect(drained.Contains("  one\n") && drained.Contains("  two\n") && drained.Contains("  three\n"),
                   "every held line reaches the disk");
            Expect(drained.IndexOf("one", StringComparison.Ordinal) < drained.IndexOf("two", StringComparison.Ordinal) &&
                   drained.IndexOf("two", StringComparison.Ordinal) < drained.IndexOf("three", StringComparison.Ordinal),
                   "in order");

            // Give the writer a moment to book the recovery, then check the header.
            for (int i = 0; i < 50 && Breadcrumbs.Status.StartsWith("STALLED"); i++)
            {
                Thread.Sleep(20);
            }

            status = Breadcrumbs.Status;
            Expect(status.StartsWith("4 lines on disk"), "the header is back to counting (" + status + ")");
            Expect(status.Contains("stalled 1 time(s)") && status.Contains("in the trail file"),
                   "and remembers the stall and where it was (" + status + ")");

            // The disk is a disk again: a line after the recovery waits for it, and
            // it is there when Drop returns.
            File.Delete(path);
            File.WriteAllText(path, "");
            clock.Restart();
            Breadcrumbs.Drop("four");
            Expect(clock.ElapsedMilliseconds < 1500 && File.ReadAllText(path).Contains("  four\n"),
                   "and a line after the recovery is waited for and on disk again (" + clock.ElapsedMilliseconds + " ms)");
        }

        /// <summary>
        /// dlog is the write that blocks. Same promises: the thread comes back, the
        /// header names dlog, and the file gets the line when dlog lets go.
        /// </summary>
        private static void DlogHang(string dir)
        {
            Expect(Breadcrumbs.InitIn(dir), "the trail starts on a real file");
            string path = Path.Combine(dir, "breadcrumbs.log");

            Tizen.Log.Gate.Reset();

            var clock = Stopwatch.StartNew();
            Breadcrumbs.Drop("held by dlog");
            long took = clock.ElapsedMilliseconds;
            Expect(took >= 1500 && took < 4000, "the dropping thread waits two seconds and comes back (" + took + " ms)");

            string status = Breadcrumbs.Status;
            Expect(status.StartsWith("STALLED — the dlog has held"), "the header names dlog (" + status + ")");
            Expect(!File.ReadAllText(path).Contains("held by dlog"), "the file does not have the line yet");

            Tizen.Log.Gate.Set();
            for (int i = 0; i < 100 && !File.ReadAllText(path).Contains("held by dlog"); i++)
            {
                Thread.Sleep(20);
            }

            Expect(File.ReadAllText(path).Contains("held by dlog"), "and gets it once dlog lets go");
            for (int i = 0; i < 50 && Breadcrumbs.Status.StartsWith("STALLED"); i++)
            {
                Thread.Sleep(20);
            }

            Expect(Breadcrumbs.Status.Contains("in the dlog"), "the header remembers which write it was (" + Breadcrumbs.Status + ")");
        }

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        private static extern int mkfifo(string path, uint mode);

        /// <summary>
        /// libc's own, not a child process: starting one from .NET right before the
        /// writer's thread is created cost that thread two seconds to start in the
        /// first version of this harness, which is not the stall being measured.
        /// </summary>
        private static bool Mkfifo(string path)
        {
            return mkfifo(path, 0x1B6) == 0;
        }

        private static void Expect(bool held, string what)
        {
            if (held)
            {
                Console.WriteLine("    ok   " + what);
                return;
            }

            _failures++;
            Console.WriteLine("    FAIL " + what);
        }
    }
}
