using System;
using System.Collections.Generic;

namespace Overscan.Harness
{
    /// <summary>
    /// Drives EngineImports.Census over the libraries run.sh built, one scenario per
    /// process because the census's books are static.
    ///
    ///   census  <needy.so> <blocked soname>   the real shape: one dependency refused
    ///   underlinked <needy.so>                a dependency that only resolves in company
    ///   clean   <lib.so>                      nothing refused, nothing unresolved
    ///   hang    <fifo>                        a file that never answers
    ///   missing <path>                        no implementation at all
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Fails = new List<string>();

        private static void Expect(bool ok, string what)
        {
            if (!ok)
            {
                Fails.Add(what);
            }
        }

        private static int Main(string[] args)
        {
            string scenario = args.Length > 0 ? args[0] : "";
            string path = args.Length > 1 ? args[1] : null;
            string blocked = args.Length > 2 ? args[2] : null;

            var started = DateTime.UtcNow;
            if (scenario == "background")
            {
                // The shape OnCreate uses ahead of the engine (issue #105): the census
                // on its own thread, the caller waiting, bounded. Same answers as
                // the inline call, and a Census behind it must find it taken.
                EngineImports.RunInBackground(path, blocked);
                Expect(EngineImports.WaitForCensus(60000), "the census on its own thread finishes inside the wait");
                Expect(EngineImports.WaitForCensus(0), "a wait after it returns at once");
                scenario = "census";
            }
            else
            {
                Expect(EngineImports.WaitForCensus(0), "before any census, a wait returns at once");
                EngineImports.Census(path, blocked);
            }

            double seconds = (DateTime.UtcNow - started).TotalSeconds;

            string summary = EngineImports.Summary;
            string dump = EngineImports.Dump();
            string trail = Breadcrumbs.Trail;
            Console.WriteLine("    summary: " + summary);

            switch (scenario)
            {
                case "census":
                    Expect(trail.Contains("engine imports: reading " + path), "the trail names the file read");
                    Expect(trail.Contains(" needed, ") && trail.Contains(" imports ("), "the trail has the counts line");
                    Expect(trail.Contains("engine imports: refused " + blocked + " — "), "the refused dependency is named with the loader's words");
                    Expect(!trail.Contains("refused libm.so.6") && !trail.Contains("refused libc.so.6"), "libraries that load are not reported refused");
                    Expect(EngineImports.Refused.Count == 1, "exactly one refusal, got " + EngineImports.Refused.Count);
                    Expect(EngineImports.Unresolved.Count == 2, "exactly the two symbols of the blocked library, got " + EngineImports.Unresolved.Count + ": " + string.Join(", ", EngineImports.Unresolved));
                    Expect(trail.Contains("  import: blocked_alpha (FUNC)"), "blocked_alpha named as a function");
                    Expect(trail.Contains("  import: blocked_counter (OBJECT)"), "blocked_counter named as a data object");
                    Expect(!trail.Contains("import: cos") && !trail.Contains("import: strlen"), "symbols another library provides are not listed");
                    Expect(!trail.Contains("import: __gmon_start__") && !trail.Contains("import: __cxa_finalize"), "weak imports are set aside");
                    Expect(dump.Contains("  needed: " + blocked) && dump.Contains("  needed: libm.so.6"), "the dump lists every DT_NEEDED");
                    Expect(dump.Contains("  loaded: libm.so.6"), "the dump says which dependencies loaded");
                    Expect(dump.Contains("provides") && (dump.Contains("libm.so.6") || dump.Contains("(process)")), "the dump says who provides what resolved");
                    Expect(summary.Contains("(1 refused: " + blocked + ")"), "the summary names the refusal");
                    Expect(summary.Contains("2 unresolved: blocked_alpha (FUNC), blocked_counter (OBJECT)"), "a short unresolved list is on the summary line");
                    Expect(trail.Contains("engine imports: done — "), "the trail ends on done");
                    break;

                case "underlinked":
                    // The AU7200's libwgt-manifest-handlers.so.1: a library whose own
                    // DT_NEEDED does not name the sibling that provides a data symbol
                    // it imports, so alone and RTLD_LOCAL it is "undefined symbol",
                    // and under the engine, with the whole list in one scope, it
                    // loads. The census must not count it as refused or its exports
                    // as a stub's work.
                    Expect(trail.Contains("engine imports: held back libunder.so — ") && trail.Contains("undefined symbol: sib_value"),
                           "the first pass holds the library back and names the symbol");
                    Expect(trail.Contains("engine imports: loaded libunder.so on the second pass"), "the second pass loads it");
                    Expect(!trail.Contains("refused libunder.so"), "it is never called refused");
                    Expect(EngineImports.Refused.Count == 0, "nothing refused, got " + EngineImports.Refused.Count);
                    Expect(EngineImports.Unresolved.Count == 0, "nothing unresolved, got: " + string.Join(", ", EngineImports.Unresolved));
                    Expect(dump.Contains("  loaded: libunder.so (second pass)"), "the dump says which pass loaded it");
                    Expect(summary.Contains("0 unresolved") && !summary.Contains("refused"), "the summary is clean: " + summary);
                    break;

                case "clean":
                    Expect(EngineImports.Refused.Count == 0, "nothing refused");
                    Expect(EngineImports.Unresolved.Count == 0, "nothing unresolved, got: " + string.Join(", ", EngineImports.Unresolved));
                    Expect(summary.Contains("0 unresolved"), "summary says 0 unresolved");
                    Expect(trail.Contains("ELF32") || trail.Contains("ELF64"), "the ELF class is named");
                    break;

                case "hang":
                    Expect(summary.Contains(Deadline.Missed), "a file that never answers is a recorded miss, got: " + summary);
                    Expect(seconds < 60, "the miss came back under the deadline (" + seconds.ToString("0.0") + "s)");
                    Expect(!trail.Contains("done —"), "no done line on a miss");
                    break;

                case "missing":
                    Expect(summary.Contains("not at any known path") || summary.Contains("reading"), "a missing implementation is said so, got: " + summary);
                    Expect(!trail.Contains("done —"), "no done line without a file");
                    break;

                default:
                    Fails.Add("unknown scenario " + scenario);
                    break;
            }

            // Whatever the scenario, a second call must be a no-op.
            EngineImports.Census(path, blocked);
            Expect(EngineImports.Summary == summary, "a second Census changed the summary");

            if (Fails.Count == 0)
            {
                Console.WriteLine("    ok");
                return 0;
            }

            foreach (string fail in Fails)
            {
                Console.WriteLine("    FAIL " + fail);
            }

            return 1;
        }
    }
}
