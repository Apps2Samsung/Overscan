using System;
using System.Diagnostics;
using System.IO;
using Tizen.Applications;

namespace Overscan.Harness
{
    /// <summary>
    /// Holds NativeProbe's ladder to the one property two builds have failed to have:
    /// that it converges. Every scenario is a shape issue #17's set has actually
    /// produced, and each of them used to end the walk where it started.
    ///
    /// run.sh lays the files out — a directory tree, a FIFO, a pre-seeded ledger — and
    /// this asserts what the walk did with them.
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("usage: probeladder <scenario> <root>");
                return 2;
            }

            string scenario = args[0];
            string root = args[1];

            Application.Layout = new Tizen.Applications.DirectoryInfo
            {
                // TizenFX hands back the resource directory with a trailing slash, and
                // AppRoot derives bin/ and lib/ from it — so the slash is part of what
                // is being tested, not a detail of the fixture.
                Resource = Path.Combine(root, "res") + "/",
                Data = Path.Combine(root, "data"),
            };

            var clock = Stopwatch.StartNew();

            switch (scenario)
            {
                case "walk":
                    Walk(root);
                    break;
                case "resume":
                    Resume(root);
                    break;
                case "hang":
                    Hang(root, clock);
                    break;
                case "killed":
                    Killed(root);
                    break;
                case "version":
                    Version(root);
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

        /// <summary>Every rung answers, so the walk reaches a verdict and writes it down.</summary>
        private static void Walk(string root)
        {
            NativeProbe.Run();

            Expect(NativeProbe.Summary != "(not asked)", "the header stops saying (not asked)");
            Expect(!NativeProbe.Summary.StartsWith("still asking"), "the header holds a verdict, not progress");
            Expect(NativeProbe.Summary.Contains("maps executable and dlopen loaded it"),
                   "this box allows all three, so the verdict is the one a stub needs");
            Expect(NativeProbe.Dump().Contains("kept across launches"),
                   "the report shows the answers that outlive a launch");

            string ledger = Ledger(root);
            Expect(ledger.StartsWith("ledger 2"), "the ledger is stamped with this build's version");
            Expect(ledger.Contains("res/:open\tok"), "the open is on the books — it was not on 3368aea's");
            Expect(ledger.Contains("res/:header\t"), "so is the header read");
            Expect(ledger.Contains("res/:read\t"), "so is the readable mapping");
            Expect(ledger.Contains("res/:exec\t"), "so is the executable mapping");
            Expect(ledger.Contains("res/:labels\t"),
                   "and so is getxattr, the call this set has hung on twice");
        }

        /// <summary>
        /// A second launch replays what the first established instead of asking again.
        /// The rungs that must not be repeated are the ones that can end a launch.
        /// </summary>
        private static void Resume(string root)
        {
            NativeProbe.Run();
            NativeProbe.Run();

            string trail = Breadcrumbs.Trail;

            Expect(trail.Contains("res/:exec: answered on an earlier launch"),
                   "the second launch replays the executable mapping");
            Expect(Occurrences(trail, "probe: mmap PROT_READ|PROT_EXEC res/") == 1,
                   "and does not ask for it a second time");
            Expect(Occurrences(trail, "probe: open res/") == 2,
                   "but does open the file again — the rungs behind it need the descriptor");
        }

        /// <summary>
        /// The shape that stopped `build-3368aea`: a call that never comes back. The
        /// walk has to write it off and finish the locations behind it in the same
        /// launch, because nothing about a hang reaches the next one.
        /// </summary>
        private static void Hang(string root, Stopwatch clock)
        {
            NativeProbe.Run();

            string ledger = Ledger(root);

            Expect(ledger.Contains("res/:open\tDID NOT RETURN"),
                   "the call that never came back is written down as such");
            Expect(NativeProbe.Dump().Contains("bin/ mmap PROT_READ|PROT_EXEC:"),
                   "and the locations behind it are still asked — the whole point");
            Expect(!NativeProbe.Summary.StartsWith("still asking"),
                   "so the launch still reaches a verdict");
            Expect(clock.ElapsedMilliseconds < 60000,
                   "and reaches it in one evening (" + clock.ElapsedMilliseconds + " ms)");
        }

        /// <summary>
        /// A rung whose name is on the ledger with no answer under it: the launch that
        /// asked never came back. It is skipped, recorded, and never asked again.
        /// </summary>
        private static void Killed(string root)
        {
            NativeProbe.Run();

            string trail = Breadcrumbs.Trail;

            Expect(trail.Contains("res/ mmap PROT_READ|PROT_EXEC: KILLED THE PROCESS"),
                   "the abandoned rung is reported as the refusal it is");
            Expect(!trail.Contains("probe: mmap PROT_READ|PROT_EXEC res/"),
                   "and is not asked again");
            Expect(Ledger(root).Contains("res/:exec\tKILLED THE PROCESS"),
                   "and its verdict is on the books for every launch after this one");
            Expect(trail.Contains("res/ dlopen:"), "the rungs behind it are still asked");
        }

        /// <summary>
        /// A ledger from an earlier build is thrown away rather than half-believed —
        /// otherwise a renamed rung reports the previous ladder's answer as its own.
        /// </summary>
        private static void Version(string root)
        {
            NativeProbe.Run();

            string trail = Breadcrumbs.Trail;

            Expect(trail.Contains("probe ledger: from another build, starting over"),
                   "a ledger stamped for another build is discarded");
            Expect(trail.Contains("probe: mmap PROT_READ|PROT_EXEC res/"),
                   "so its answers are asked for again rather than replayed");
            Expect(Ledger(root).StartsWith("ledger 2"), "and the file is re-stamped");
        }

        private static string Ledger(string root)
        {
            string path = Path.Combine(root, "data", "probe-ledger.txt");
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static int Occurrences(string haystack, string needle)
        {
            int found = 0;
            int at = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (at >= 0)
            {
                found++;
                at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
            }

            return found;
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
