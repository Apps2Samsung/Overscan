using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>Where the ledger lives, in the one directory that survives a launch.</summary>
        private const string LedgerFile = "probe-ledger.txt";

        /// <summary>
        /// Bumped whenever the steps change their names or their meaning. A ledger
        /// stamped with anything else is thrown away rather than half-believed —
        /// otherwise the first run of a new build would report the previous build's
        /// ladder as already answered.
        ///
        /// `ledger 3`: a bare step name no longer means <see cref="Killed"/> on its
        /// own — see <see cref="FatalAfter"/> — and every launch of the walk writes a
        /// <see cref="LaunchKey"/> line, which is now where the launch count comes from.
        /// </summary>
        private const string LedgerVersion = "ledger 3";

        /// <summary>What a step's result reads as when the launch that ran it never came back.</summary>
        private const string Killed = "KILLED THE PROCESS";

        /// <summary>
        /// How many launches a rung has to end before it is recorded as
        /// <see cref="Killed"/> and never asked again.
        ///
        /// One used to be enough, and issue #17's set showed on 2026-09-05 why it is
        /// not: the ledger came back with `control:anon-exec` begun and unanswered —
        /// an anonymous `PROT_EXEC` page, the one rung on the ladder that set had
        /// already answered `ok` three times over, on `build-85d0e4e` twice and on
        /// `build-3368aea` once. Whatever ended that launch, it was not the mmap. On a
        /// set that stops launches at moments of its own choosing, a rung that was in
        /// flight when one stopped is a rung with bad luck, not a refusal; the same
        /// rung in flight twice is a pattern. A false refusal here is the worst answer
        /// this ladder can give — the verdict that says "nowhere left to put a file"
        /// closes the issue — so a rung gets a second launch before it is believed.
        /// </summary>
        private const int FatalAfter = 2;

        /// <summary>
        /// What a step's result reads as when the call was still inside the kernel when
        /// the watchdog gave up waiting for it.
        ///
        /// A separate answer from <see cref="Killed"/>, and the distinction is the
        /// point: a launch that ends is visible from the next launch, and a call that
        /// never returns is visible from nowhere at all. On issue #17's set the second
        /// is the ordinary case — `getxattr` has hung it twice — and a walk parked on
        /// one of those is indistinguishable, in the report, from a walk that was never
        /// asked to start.
        /// </summary>
        private const string Stalled = Deadline.Missed;

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

        /// <summary>
        /// The one line for the report header.
        ///
        /// Until this launch's walk has begun it is whatever an earlier launch left on
        /// the ledger — the verdict, if one was reached, or how far the walk got. Every
        /// report issue #17's set has ever sent was a page loaded a second or two after
        /// launch, and until `build-f295172` such a page read `(not asked)` no matter
        /// what was already on disk from the launch before. The half of the probe that
        /// outlives a launch is the half a launch-time page has to show.
        /// </summary>
        public static string Summary
        {
            get { return _summary ?? Ledger.Peek(); }
            private set { _summary = value; }
        }

        private static string _summary;

        /// <summary>The header before anything has been asked, on this launch or any other.</summary>
        private const string NotAsked = "(not asked)";

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

            /// <summary>True if asking about this one is what ended a launch.</summary>
            public bool KilledUs;

            /// <summary>True if a call about this one never came back at all.</summary>
            public bool Hung;
        }

        /// <summary>
        /// What has already been asked, kept on disk so the ladder survives its own
        /// questions.
        ///
        /// The Q80 answered `build-85d0e4e` by dying on the first rung — the trail
        /// ends on `probe: mmap PROT_READ|PROT_EXEC via /proc/self/fd res/` and the
        /// four locations behind it were never asked at all. So each call now writes
        /// its own name to a file before it is made and its answer to the same file
        /// after: a launch that does not come back leaves a name with no answer, and
        /// the next launch reads that as <see cref="Killed"/>, skips it, and carries
        /// on to the next rung.
        ///
        /// `build-3368aea` shipped exactly that and the same set stopped the ladder
        /// again, in the one window this did not cover. Its trail ends on
        /// `probe: open res/`, between the open and the header read — three calls that
        /// were left unledgered because nothing had ever refused them, and therefore
        /// the three with nothing written down about them. The next launch walked into
        /// the same one, and would have done so forever. Two things follow, and both
        /// are in here now:
        ///
        /// * <b>Every rung is ledgered, not the ones predicted to be dangerous.</b>
        ///   That prediction has been wrong twice and in both directions: the rung
        ///   expected to be fatal answered `EPERM` politely, and one of the three
        ///   nobody instrumented is what stopped the walk.
        /// * <b>A call that never returns is not a call that killed the launch.</b>
        ///   This file could only ever learn from a dead process, and the probe runs
        ///   on a background thread — so a call stuck in the kernel leaves the app
        ///   alive, the walk parked, and the report reading `(not asked)` with nothing
        ///   to separate the two. Every call therefore goes out under a watchdog and a
        ///   hang is recorded as <see cref="Stalled"/> in the *same* launch, so a set
        ///   that hangs on every rung still finishes the ladder without anybody
        ///   opening the app again.
        ///
        /// `build-f295172` then stopped in front of all of that. Its trail has the
        /// engine failure and then nothing, for the 84 seconds until the next launch,
        /// and the only code between the start of the walk and its first trail line
        /// was this file's own I/O: an <c>open</c>, a read, and two appends each ending
        /// in <c>fsync</c> — the one stretch of the path not under the watchdog,
        /// because it was ours rather than the set's. Three things follow from that
        /// and are in here now:
        ///
        /// * <b>The ledger's own I/O goes out under <see cref="Deadline"/></b>, like
        ///   every rung behind it. A ledger that does not open in five seconds is
        ///   written off — the walk runs without one — and says so on the trail.
        /// * <b>No <c>fsync</c>.</b> <c>Breadcrumbs</c> has written thousands of lines
        ///   on that set with a plain flush and never lost one to a crash; the ledger's
        ///   two syncs bought nothing and sat exactly where the walk went silent.
        /// * <b>The verdict is on the ledger, and the report reads the ledger without
        ///   the walk.</b> <see cref="Conclude"/> writes the one line that matters, and
        ///   <see cref="Peek"/> reads the file for a page served before this launch's
        ///   walk has begun — which is when every page from that set has been fetched.
        ///
        /// This is the same idea as <c>Breadcrumbs</c> — write before the call, not
        /// after — carried one step further: the trail says which call died, and the
        /// ledger makes the *next* launch act on that.
        /// </summary>
        private static class Ledger
        {
            /// <summary>Steps that have an answer, from this launch or an earlier one.</summary>
            private static readonly Dictionary<string, string> Answered =
                new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>
            /// Steps a previous launch began and never finished, and how many launches
            /// each has ended that way. See <see cref="FatalAfter"/>.
            /// </summary>
            private static readonly Dictionary<string, int> Abandoned =
                new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>
            /// The order steps were first heard of, so the report reads in the order
            /// the walk ran rather than in whatever order a hash table offers them.
            /// </summary>
            private static readonly List<string> Order = new List<string>();

            /// <summary>
            /// Guards the three above. The report is served from the socket thread
            /// while the walk is still writing — and nothing here may be held across
            /// <see cref="Trace"/> or a probe call, because <see cref="Dump"/> takes
            /// the trail's lock first and this one second.
            /// </summary>
            private static readonly object Book = new object();

            private static string _path;

            /// <summary>The verdict a walk reached, this launch or an earlier one. Under <see cref="Book"/>.</summary>
            private static string _verdict;

            /// <summary>Set once <see cref="Peek"/> has read the file, so a report reads it once.</summary>
            private static bool _peeked;

            /// <summary>The line the verdict is kept under. Not a step, so never asked.</summary>
            private const string VerdictKey = "verdict";

            /// <summary>
            /// Written once per launch of the walk, before its first question. Counting
            /// these is how <see cref="Launches"/> is known; it used to be inferred
            /// from the abandoned steps, which stopped being the same thing once a
            /// step could be abandoned and asked again.
            /// </summary>
            private const string LaunchKey = "launch";

            /// <summary>True when there is nowhere to keep one, so nothing resumes.</summary>
            public static bool Unavailable
            {
                get { return _path == null; }
            }

            /// <summary>
            /// Reads whatever previous launches left. A file stamped for a different
            /// build is deleted rather than parsed.
            ///
            /// Under the deadline, because on `build-f295172` this is where the walk
            /// went silent: nothing on the trail after the engine failure, and this was
            /// the only code in front of the first line the walk writes. A ledger that
            /// does not open is written off and the walk runs without one — it cannot
            /// resume and cannot be resumed from, and the trail says so — but it runs.
            /// </summary>
            public static void Open(string directory)
            {
                _path = null;

                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                string path = Path.Combine(directory, LedgerFile);
                string outcome = Deadline.Run(delegate
                {
                    string[] lines = File.Exists(path) ? File.ReadAllLines(path) : new string[0];
                    if (lines.Length > 0 && string.Equals(lines[0], LedgerVersion, StringComparison.Ordinal))
                    {
                        Parse(lines);
                        Append(path, LaunchKey);
                        return "ok";
                    }

                    bool other = lines.Length > 0;
                    if (other)
                    {
                        File.Delete(path);
                    }

                    Parse(new string[0]);
                    Append(path, LedgerVersion);
                    Append(path, LaunchKey);
                    return other ? "from another build, starting over" : "new";
                });

                if (string.Equals(outcome, "ok", StringComparison.Ordinal) ||
                    string.Equals(outcome, "new", StringComparison.Ordinal) ||
                    outcome.StartsWith("from another build", StringComparison.Ordinal))
                {
                    // This launch is one of them, whatever the file said — its own
                    // marker is on the file now, and Parse only counted the others.
                    lock (Book)
                    {
                        Launches++;
                    }

                    if (!string.Equals(outcome, "ok", StringComparison.Ordinal) &&
                        !string.Equals(outcome, "new", StringComparison.Ordinal))
                    {
                        Trace("  probe ledger: " + outcome);
                    }

                    _path = path;
                    return;
                }

                // Stalled, or threw. Without a ledger the probe still works, it just
                // cannot resume — and whatever Peek read for an earlier report is left
                // on the books, so answers already established are still replayed.
                Trace("  probe ledger unavailable: " + outcome +
                      " — this launch cannot resume, and cannot be resumed from");
                _path = null;
            }

            /// <summary>
            /// Where the ladder stands on this install, read from the file under the
            /// deadline and never written: "no ledger", "finished" (a verdict is on it),
            /// "unfinished", or "unfinished — another build's ledger". For
            /// <see cref="StartEarlyIfUnfinished"/>, which is the only caller.
            /// </summary>
            public static string Standing(string path)
            {
                return Deadline.Run(delegate
                {
                    if (!File.Exists(path))
                    {
                        return "no ledger";
                    }

                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length == 0 || !string.Equals(lines[0], LedgerVersion, StringComparison.Ordinal))
                    {
                        return "unfinished — another build's ledger";
                    }

                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (lines[i].StartsWith(VerdictKey + "\t", StringComparison.Ordinal))
                        {
                            return "finished";
                        }
                    }

                    return "unfinished";
                });
            }

            /// <summary>
            /// What earlier launches left, for a report served before this launch's
            /// walk has begun. Read once, under the deadline, and never written: a
            /// page must not be what starts the ladder, or what stalls the socket
            /// thread serving it.
            /// </summary>
            public static string Peek()
            {
                lock (Book)
                {
                    if (_peeked)
                    {
                        return Order.Count == 0 && _verdict == null ? NotAsked : Progress();
                    }
                }

                string directory = DataDirectory();
                if (string.IsNullOrEmpty(directory))
                {
                    // Asked before the application object exists. Not remembered as
                    // read, so a later page tries again.
                    return NotAsked;
                }

                lock (Book)
                {
                    _peeked = true;
                }

                string path = Path.Combine(directory, LedgerFile);
                string outcome = Deadline.Run(delegate
                {
                    if (!File.Exists(path))
                    {
                        return "none";
                    }

                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length == 0 || !string.Equals(lines[0], LedgerVersion, StringComparison.Ordinal))
                    {
                        return "from another build";
                    }

                    Parse(lines);
                    return "ok";
                });

                return string.Equals(outcome, "ok", StringComparison.Ordinal) ? Progress() : NotAsked;
            }

            /// <summary>
            /// Replaces the books with what <paramref name="lines"/> say. The first
            /// line is the version stamp, already checked. Under <see cref="Book"/>.
            /// </summary>
            private static void Parse(string[] lines)
            {
                lock (Book)
                {
                    Answered.Clear();
                    Abandoned.Clear();
                    Order.Clear();
                    _verdict = null;

                    // A name on its own is a call that was started; the same name
                    // with an answer after it retires that. Later lines win, which
                    // is what makes the file append-only. A name left bare twice by
                    // two launches is counted twice — that count is what decides
                    // whether the step is asked again (see FatalAfter).
                    int launches = 0;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        int split = line.IndexOf('\t');
                        if (split < 0)
                        {
                            if (string.Equals(line, LaunchKey, StringComparison.Ordinal))
                            {
                                launches++;
                                continue;
                            }

                            int ended;
                            Abandoned.TryGetValue(line, out ended);
                            Abandoned[line] = ended + 1;
                            Remember(line);
                        }
                        else
                        {
                            string step = line.Substring(0, split);
                            if (string.Equals(step, VerdictKey, StringComparison.Ordinal))
                            {
                                _verdict = line.Substring(split + 1);
                                continue;
                            }

                            Abandoned.Remove(step);
                            Answered[step] = line.Substring(split + 1);
                            Remember(step);
                        }
                    }

                    // The launches the walk has started so far, not counting one that
                    // has not written its own marker yet. Open adds this launch.
                    Launches = launches;
                }
            }

            /// <summary>
            /// Writes the verdict down. It is the one line the whole ladder exists to
            /// produce, and until `build-f295172` it was the one line that never
            /// reached the disk: a page loaded on the launch after the walk finished
            /// read `(not asked)` over a complete ledger.
            /// </summary>
            public static void Conclude(string verdict)
            {
                lock (Book)
                {
                    _verdict = verdict;
                }

                Append(VerdictKey + "\t" + verdict);
            }

            /// <summary>
            /// Makes one call that might not come back, or reports what happened the
            /// last time it was tried.
            /// </summary>
            public static string Ask(string step, string announcement, Func<string> call)
            {
                return Ask(step, announcement, call, false);
            }

            /// <summary>
            /// The same, for a call whose <i>effect</i> the rungs behind it need rather
            /// than its answer: the `open` whose descriptor the next four read from,
            /// the copy into `data/` that has to be on disk before anything can be
            /// asked about it. A remembered "ok" is neither of those, so a
            /// <paramref name="repeatable"/> step is made again on every launch — but a
            /// remembered answer that ended or hung a launch still retires it, which is
            /// the half that has to be remembered.
            /// </summary>
            public static string Ask(string step, string announcement, Func<string> call, bool repeatable)
            {
                // The bare answer is what comes back, never a decorated one: callers
                // test it with Succeeded, which reads the end of the string.
                string remembered = Remembered(step);
                if (remembered != null && (!repeatable || IsFatal(remembered)))
                {
                    Trace("  " + step + ": answered on an earlier launch — " + remembered);
                    return remembered;
                }

                int ended = remembered == null ? AbandonedCount(step) : 0;
                if (ended >= FatalAfter)
                {
                    // The strongest refusal there is: not an errno, but the end of the
                    // launch that asked — twice, by two launches, see FatalAfter.
                    // Recorded so it is never asked again.
                    Record(step, Killed);
                    return Killed;
                }

                if (ended > 0)
                {
                    Trace("  " + step + ": the launch that asked this ended before it answered — " +
                          "asking once more; a second launch ending here is a refusal");
                }

                Append(step);
                Trace(announcement);

                string answer = Watched(step, call);
                Record(step, answer);
                return answer;
            }

            /// <summary>
            /// One call, with a deadline — see <see cref="Deadline"/>, where the
            /// mechanism lives now that the engine explainer and this ledger's own
            /// <c>open</c> need it too. A miss is named on the trail here, in the
            /// walk's own words.
            /// </summary>
            private static string Watched(string step, Func<string> call)
            {
                string answer = Deadline.Run(call);
                if (string.Equals(answer, Stalled, StringComparison.Ordinal))
                {
                    Trace("  " + step + ": nothing back in " +
                          (Deadline.DefaultMs / 1000).ToString(CultureInfo.InvariantCulture) +
                          "s — written off, carrying on");
                }

                return answer;
            }

            /// <summary>
            /// The header line while the walk is still going — and on a set that hangs
            /// a rung, that is most of a minute per location.
            ///
            /// Both reports on `build-3368aea` came back reading
            /// `own native : (not asked)` from a launch whose trail plainly showed the
            /// probe running, because the verdict was only written once the whole walk
            /// returned. The page was loaded a second after launch, which is a
            /// reasonable thing to do and has now cost two rounds of somebody's evening.
            /// </summary>
            public static string Progress()
            {
                lock (Book)
                {
                    if (_verdict != null)
                    {
                        // Reached on this launch or an earlier one; either way it is
                        // the answer, and a walk re-running behind it only confirms it.
                        return _verdict;
                    }

                    if (Order.Count == 0)
                    {
                        return "(not asked yet)";
                    }

                    var refused = new List<string>();
                    foreach (string step in Order)
                    {
                        string answer;
                        if (Answered.TryGetValue(step, out answer) && IsFatal(answer))
                        {
                            refused.Add(step + " " + answer);
                        }
                    }

                    return "still asking — " +
                           Answered.Count.ToString(CultureInfo.InvariantCulture) + " answered" +
                           (refused.Count == 0 ? "" : ", " + string.Join(", ", refused.ToArray())) +
                           " (see the block below; open the app again if it stops here)";
                }
            }

            /// <summary>
            /// Every answer on the books, for the report. This is the half of the probe
            /// that outlives a launch, and on a set that stops the walk it is the only
            /// half a page has to show.
            /// </summary>
            public static string Recorded()
            {
                lock (Book)
                {
                    if (Order.Count == 0)
                    {
                        return "";
                    }

                    var text = new List<string>();
                    text.Add("  kept across launches (" + LedgerVersion + ", launch " +
                             Launches.ToString(CultureInfo.InvariantCulture) + ")");

                    foreach (string step in Order)
                    {
                        string answer;
                        if (Answered.TryGetValue(step, out answer))
                        {
                            text.Add("    " + step.PadRight(22) + " = " + answer);
                            continue;
                        }

                        int ended;
                        Abandoned.TryGetValue(step, out ended);
                        text.Add("    " + step.PadRight(22) + " = " +
                                 (ended >= FatalAfter
                                      ? "(began and never answered on " + ended.ToString(CultureInfo.InvariantCulture) +
                                        " launches — the next walk records this as " + Killed + ")"
                                      : "(began and never answered on " + ended.ToString(CultureInfo.InvariantCulture) +
                                        " launch — asked once more before it counts as a refusal)"));
                    }

                    if (_verdict != null)
                    {
                        text.Add("    " + VerdictKey.PadRight(22) + " = " + _verdict);
                    }

                    return string.Join("\n", text.ToArray()) + "\n";
                }
            }

            /// <summary>
            /// How many launches this ladder has taken so far, counting this one: the
            /// <see cref="LaunchKey"/> lines on the file. It goes in the verdict
            /// because a number above one means the set ended launches rather than
            /// answer, and that is a finding rather than an accident.
            /// </summary>
            public static int Launches = 1;

            /// <summary>What one launch of the walk has already been told. Under <see cref="Book"/>.</summary>
            private static string Remembered(string step)
            {
                lock (Book)
                {
                    string answer;
                    return Answered.TryGetValue(step, out answer) ? answer : null;
                }
            }

            /// <summary>How many launches ended with this step begun and unanswered.</summary>
            private static int AbandonedCount(string step)
            {
                lock (Book)
                {
                    int ended;
                    return Abandoned.TryGetValue(step, out ended) ? ended : 0;
                }
            }

            private static void Record(string step, string answer)
            {
                lock (Book)
                {
                    Answered[step] = answer;
                    Abandoned.Remove(step);
                    Remember(step);
                }

                Append(step + "\t" + answer);
            }

            /// <summary>Adds a step to the report's order. Called under <see cref="Book"/>.</summary>
            private static void Remember(string step)
            {
                if (!Order.Contains(step))
                {
                    Order.Add(step);
                }
            }

            /// <summary>
            /// Whether an answer is one never to ask for again: the launch that asked
            /// it ended, or the call never came back. Both are refusals harder than
            /// <c>EPERM</c>, and both are permanent as far as this ladder is concerned.
            /// </summary>
            private static bool IsFatal(string answer)
            {
                return string.Equals(answer, Killed, StringComparison.Ordinal) ||
                       string.Equals(answer, Stalled, StringComparison.Ordinal);
            }

            /// <summary>
            /// One line, flushed to disk on its own. The whole point is that it is on
            /// the disk before the call it describes, so it is opened and closed each
            /// time rather than held — the same trade <c>Breadcrumbs</c> makes.
            ///
            /// A plain flush, not an <c>fsync</c>. The sync was here until
            /// `build-f295172`, on the theory that a line the kernel had not yet written
            /// out could be lost to the crash it describes — but <c>Breadcrumbs</c> has
            /// made the same trade without one on every set this app has run on,
            /// including issue #17's, and has never lost a line to a crash. What the
            /// sync did do is sit in the one window on that set where the walk went
            /// silent before its first line, twice per launch, on a filesystem that
            /// parks calls it does not like. It bought nothing and may have cost the
            /// build.
            /// </summary>
            private static void Append(string line)
            {
                if (_path != null)
                {
                    Append(_path, line);
                }
            }

            /// <summary>
            /// Under <see cref="Deadline"/>, like the open: on issue #17's set a write
            /// to our own `data/` is a call like any other, and the 2026-09-05 report
            /// has a launch whose last act was an append here. An append that misses
            /// writes the ledger off for the rest of this launch — one line on the
            /// trail, not one five-second wait per rung — and the walk carries on with
            /// its books behind it, which the next launch reads as "ask again".
            /// </summary>
            private static void Append(string path, string line)
            {
                string outcome = Deadline.Run(delegate
                {
                    using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(file))
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }

                    return "ok";
                });

                if (!string.Equals(outcome, "ok", StringComparison.Ordinal))
                {
                    // Best-effort, like everything else that reaches past managed code —
                    // but said once, and not tried again this launch.
                    _path = null;
                    Trace("  probe ledger: writing \"" + line + "\" " + outcome +
                          " — written off for this launch; the walk carries on without it");
                }
            }
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
        /// <b>Every call in the walk goes through <see cref="Ledger"/>, including the
        /// ones nothing has ever refused.</b> Two builds have been spent on the other
        /// arrangement: `build-85d0e4e` ledgered nothing and came back having answered
        /// one location of five, `build-3368aea` ledgered the three rungs that looked
        /// dangerous and the set stopped on one of the three that did not. There is no
        /// rung left worth calling safe, and a ledger entry costs one line on a disk.
        ///
        /// Labels and mounts are read for every location after the verdict rather than
        /// beside each one. They only ever explain <i>why</i>, and `getxattr` is the
        /// call this set has not come back from twice — so nothing that decides the
        /// question is queued behind it. They are ledgered too, which is what makes
        /// them collectable at all: a hang on the first location used to eat every
        /// reading behind it, on every launch, forever.
        /// </summary>
        public static void Run()
        {
            if (Interlocked.CompareExchange(ref _walking, 1, 0) != 0)
            {
                // Already on its way — see StartEarlyIfUnfinished. Two walks on two
                // threads would leave a trail nobody can order and a ledger with every
                // line twice.
                Trace("native probe: already walking on another thread");
                return;
            }

            try
            {
                Walk();
            }
            finally
            {
                Interlocked.Exchange(ref _walking, 0);
            }
        }

        /// <summary>1 while a walk is in progress on some thread.</summary>
        private static int _walking;

        /// <summary>
        /// Walks the ladder now, on a thread of its own, if an earlier launch on this
        /// install got as far as starting it and it has not reached a verdict.
        /// Returns whether it did.
        ///
        /// The ladder normally runs after the engine has failed, behind the retry,
        /// the explainer and the permission probe — the rule since #13 has been that
        /// a diagnostic which can end the process goes after the thing it is meant to
        /// explain, never in front of it. This is the one exception, and the evidence
        /// for it is on the disk: the ledger only ever comes into existence on a
        /// launch whose engine had already failed, so a ledger with no verdict means
        /// the engine's failure on this install is recorded, ten builds over, and the
        /// ladder's verdict is not. On issue #17's set the six native calls between
        /// the first `refcount=0` and the start of the walk — the implementation's
        /// preload, `ewk_set_arguments`, the retry, the explainer's two dlopens and
        /// its directory listing — each stall now and then, and on 2026-09-05 they
        /// stopped four launches out of five in front of the ladder. Every one of
        /// those launches was spent re-establishing a failure that was already on
        /// the books. So on an install where the books already say so, the ladder
        /// goes first and the engine is asked afterwards, and the worst the walk can
        /// do to the engine's start is be in the process — one library with one
        /// symbol, if a location ever takes it — while ewk_init runs.
        ///
        /// A ledger from another build counts as unfinished: whatever it says, this
        /// build's ladder has not been walked here. An install with no ledger at all
        /// gets the ordinary order, which is every set this app works on.
        /// </summary>
        public static bool StartEarlyIfUnfinished()
        {
            string directory = DataDirectory();
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            string outcome = Ledger.Standing(Path.Combine(directory, LedgerFile));
            if (!outcome.StartsWith("unfinished", StringComparison.Ordinal))
            {
                return false;
            }

            Trace("native probe: the engine failed on an earlier launch here and the ladder is " +
                  outcome + " — walking it now, ahead of the engine");
            RunInBackground();
            return true;
        }

        /// <summary>
        /// <see cref="Run"/> on a background thread. The walk is claimed here, before
        /// the thread exists, so a <see cref="Run"/> that follows on another thread
        /// finds it taken rather than racing it.
        /// </summary>
        public static void RunInBackground()
        {
            if (Interlocked.CompareExchange(ref _walking, 1, 0) != 0)
            {
                Trace("native probe: already walking on another thread");
                return;
            }

            try
            {
                var thread = new Thread(delegate ()
                {
                    try
                    {
                        Walk();
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _walking, 0);
                    }
                });
                thread.IsBackground = true;
                thread.Name = "native-probe";
                thread.Start();
            }
            catch (Exception ex)
            {
                // A set that will not give us a thread gets asked the dangerous way.
                Trace("native probe: no thread (" + ex.GetType().Name + "), walking inline");
                try
                {
                    Walk();
                }
                finally
                {
                    Interlocked.Exchange(ref _walking, 0);
                }
            }
        }

        private static void Walk()
        {
            try
            {
                // Before the ledger, which is the first thing here that touches the
                // disk. On build-f295172 the trail ended on the engine failure and the
                // walk never wrote a line, and there was no telling a thread that never
                // ran from one parked in the ledger's own open. Now there is.
                Trace("native probe: starting");

                Ledger.Open(DataDirectory());
                if (Ledger.Unavailable)
                {
                    Trace("  probe ledger: none — a fatal step will not be skipped next launch");
                }

                // Before the first question, and again after every location: both
                // reports on build-3368aea were pages loaded a second after launch, and
                // they read `own native : (not asked)` on a set whose trail plainly
                // showed the probe running. The header now says how far the ladder has
                // got instead of nothing at all.
                Summary = Ledger.Progress();

                string anonymous = Ledger.Ask(
                    "control:anon-exec",
                    "native probe: anonymous PROT_EXEC control",
                    delegate { return MapAnonymousExecutable(); });
                Trace("  anonymous exec memory: " + anonymous);

                var locations = Locate();
                if (locations.Count == 0)
                {
                    Summary = "not shipped in this package";
                    Trace("native probe: " + Summary);
                    return;
                }

                Trace("  engine : " + Ledger.Ask(
                    "control:engine-header",
                    "  probe: read the engine's own ELF header",
                    delegate { return HeaderOf(EngineLibrary); }));

                foreach (Location location in locations)
                {
                    Ask(location);
                    Summary = Ledger.Progress();
                }

                Summary = Verdict(locations, anonymous);
                Trace("native probe verdict: " + Summary);
                Ledger.Conclude(Summary);

                // Last, and only for the record: which mount each copy sat on and
                // what Smack wrote on it. See the note above about ordering.
                foreach (Location location in locations)
                {
                    Trace("  " + location.Name + " mount: " + Ledger.Ask(
                        location.Name + ":mount",
                        "  probe: mount of " + location.Name,
                        delegate { return SmackWall.MountOf(location.Path); }));

                    Trace("  " + location.Name + " labels: " + Ledger.Ask(
                        location.Name + ":labels",
                        "  probe: getxattr on " + location.Name,
                        delegate { return LabelsOf(location.Path); }));
                }
            }
            catch (Exception ex)
            {
                Summary = "could not be asked: " + ex.GetType().Name + ": " + ex.Message;
                Trace("  " + Summary);
            }
        }

        /// <summary>
        /// Everything the report has to show: what earlier launches wrote down, then
        /// this launch's trail. The ledger goes first because it is the half that
        /// survives, and on a set that stops the walk it is the only half there is.
        /// </summary>
        public static string Dump()
        {
            // The header has usually been read first and done this already; a page
            // that asks for the block alone gets the same answer.
            if (_summary == null)
            {
                Ledger.Peek();
            }

            // Read before the trail's lock is taken, never under it: Ledger has a lock
            // of its own, and Ask holds that one while it Traces into this one.
            string kept = Ledger.Recorded();

            lock (Gate)
            {
                if (Lines.Count == 0)
                {
                    return kept.Length == 0 ? "  (not probed)\n" : kept;
                }

                return kept + string.Join("\n", Lines.ToArray()) + "\n";
            }
        }

        /// <summary>
        /// The readings, for one copy of the library: it can be opened, its header
        /// says what it was built for, a page of it maps readable, a page of it maps
        /// executable, and the dynamic loader will take it. They fail for unrelated
        /// reasons, so they are asked separately — and separately is now also how they
        /// are ledgered, because the walk has been stopped once by each half of this
        /// list and the half that stopped it was the half nobody instrumented.
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
                // The open is made again on every launch even once it has answered:
                // the four rungs behind it read from its descriptor, and a remembered
                // "ok" is not a descriptor. What is remembered is the only part that
                // matters — that asking it once ended or hung a launch.
                int opened = -1;
                string readable = Ledger.Ask(
                    location.Name + ":open",
                    "  probe: open " + location.Name + " " + location.Path,
                    delegate
                    {
                        opened = open(location.Path, ORdonly);
                        return opened < 0
                            ? "cannot even read it — " + Errno(Marshal.GetLastWin32Error())
                            : "ok";
                    },
                    true);

                if (!Succeeded(readable))
                {
                    // Nothing behind this can be asked without a descriptor, so the
                    // location stands as a refusal and the walk goes to the next one.
                    Trace("  " + location.Name + ": " + readable);
                    Note(location, readable);
                    return;
                }

                fd = opened;

                // e_flags carries ARM's float-ABI bits, and the engine's own library
                // is the only statement of what this firmware expects. A dlopen
                // refused for an ABI mismatch and one refused by policy read the
                // same from here unless those two numbers are side by side.
                string header = Ledger.Ask(
                    location.Name + ":header",
                    "  probe: read header of " + location.Name,
                    delegate { return Header(fd); },
                    true);
                Trace("  " + location.Name + " ours  : " + header);
                Note(location, header);

                string readOnly = Ledger.Ask(
                    location.Name + ":read",
                    "  probe: mmap PROT_READ " + location.Name,
                    delegate { return Map(fd, ProtRead, "PROT_READ"); },
                    true);
                Trace("  " + location.Name + " mmap " + readOnly);
                Note(location, readOnly);

                int held = fd;
                string executable = Ledger.Ask(
                    location.Name + ":exec",
                    "  probe: mmap PROT_READ|PROT_EXEC " + location.Name,
                    delegate { return Map(held, ProtRead | ProtExec, "PROT_READ|PROT_EXEC"); });
                Trace("  " + location.Name + " mmap PROT_READ|PROT_EXEC: " + executable);
                location.MapsExecutable = Succeeded(executable);
                Note(location, executable);

                if (!location.MapsExecutable)
                {
                    string reopened = Ledger.Ask(
                        location.Name + ":exec-procfd",
                        "  probe: mmap PROT_READ|PROT_EXEC via /proc/self/fd " + location.Name,
                        delegate { return MapThroughProcFd(held); });
                    Trace("  " + location.Name + " mmap via /proc/self/fd: " + reopened);
                    location.MapsExecutable = Succeeded(reopened);
                    Note(location, reopened);
                }

                string loaded = Ledger.Ask(
                    location.Name + ":dlopen",
                    "  probe: dlopen " + location.Name,
                    delegate { return Load(location.Path); });
                Trace("  " + location.Name + " dlopen: " + loaded);
                location.Loads = loaded.IndexOf("resolved", StringComparison.Ordinal) >= 0;
                Note(location, loaded);
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
        /// Records that a reading was one of the two refusals the verdict names
        /// separately: the launch that asked ended, or the call never came back.
        /// </summary>
        private static void Note(Location location, string reading)
        {
            location.KilledUs |= WasKilled(reading);
            location.Hung |= WasStalled(reading);
        }

        /// <summary>
        /// The verdict line, which is the whole point of the build: whether there is
        /// any location left with a shim behind it.
        /// </summary>
        private static string Verdict(IList<Location> locations, string anonymous)
        {
            var refused = new List<string>();
            var fatal = new List<string>();
            var hung = new List<string>();
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
                if (location.KilledUs)
                {
                    fatal.Add(location.Name);
                }

                if (location.Hung)
                {
                    hung.Add(location.Name);
                }
            }

            return "REFUSED in " + string.Join(", ", refused.ToArray()) +
                   (fatal.Count == 0
                       ? ""
                       : " — and asking " + string.Join(", ", fatal.ToArray()) + " ended the launch") +
                   (hung.Count == 0
                       ? ""
                       : " — and asking " + string.Join(", ", hung.ToArray()) + " never came back") +
                   " — anonymous exec memory " +
                   (Succeeded(anonymous) ? "is allowed, so this is about files we ship" : anonymous) +
                   (Ledger.Launches > 1
                       ? " — took " + Ledger.Launches.ToString(CultureInfo.InvariantCulture) + " launches"
                       : "");
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

                // Through the ledger like every other call that touches the file. This
                // one is asked again on every launch — the copy has to be there before
                // anything can be asked about it, and a remembered "ok" is not a file —
                // but a launch it ended or hung is remembered and never repeated.
                string copied = Ledger.Ask(
                    "control:copy-data",
                    "  probe: copy " + ProbeLibrary + " to data/",
                    delegate { return CopyFile(source, target); },
                    true);

                return Succeeded(copied) ? target : null;
            }
            catch (Exception ex)
            {
                Trace("  probe: cannot copy to data/ — " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static string CopyFile(string source, string target)
        {
            try
            {
                File.Copy(source, target, true);

                // Not needed to map a file executable, but a mode a real library
                // would carry, so a refusal cannot be read as being about the bits.
                chmod(target, ExecutableMode);
                return "ok";
            }
            catch (Exception ex)
            {
                return "cannot copy — " + ex.GetType().Name + ": " + ex.Message;
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
        /// Whether that reading is the ledger saying the launch which asked never came
        /// back. It is a refusal like any other for the purposes of the verdict, and a
        /// harder one than <c>EPERM</c>: worth naming separately because a set that
        /// kills the asker is a set no stub is going to load on either.
        /// </summary>
        private static bool WasKilled(string reading)
        {
            return string.Equals(reading, Killed, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether the call never came back at all — the watchdog's answer rather than
        /// the kernel's. Worth naming separately for the opposite reason: nothing about
        /// it says the request was refused, only that this firmware will not finish
        /// answering it, which is just as fatal to a stub that would have to be mapped
        /// during every start-up.
        /// </summary>
        private static bool WasStalled(string reading)
        {
            return string.Equals(reading, Stalled, StringComparison.Ordinal);
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
