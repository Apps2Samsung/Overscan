using System;
using System.Diagnostics;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Times a native call that might not come back — or, at a slower beat, says
    /// that the process is still here at all.
    ///
    /// Issue #17's trail ends on <c>Chromium.Initialize()</c> every launch, so
    /// ewk_init does not return — but "did not return" covers two very different
    /// failures, and they need opposite fixes:
    ///
    /// <list type="bullet">
    ///   <item>a crash inside the engine, which stops the process at once; or</item>
    ///   <item>the launcher killing us, because <c>ewk_init</c> is called from
    ///   inside the create callback and Tizen's launchpad will not wait for it
    ///   forever. That one is fixable from our side — the engine would be brought
    ///   up after the callback returns instead of during it.</item>
    /// </list>
    ///
    /// A tick a second tells them apart: a crash leaves no ticks or one, a watchdog
    /// leaves a run of them ending on a suspiciously round number.
    ///
    /// The same set then produced the opposite silence. `build-f295172`'s trail
    /// ends on `ENGINE FAILURE` — the failure screen's cue — and has nothing for
    /// the 84 seconds until the next launch, from a probe thread that should have
    /// written twenty lines in that time. That is either a process that died drawing
    /// the failure screen or a thread parked in its first syscall, and a trail with
    /// no line of any kind cannot tell those apart. So the failure screen runs a
    /// slow beat, every ten seconds for three minutes: a launch that leaves ticks
    /// and no probe lines has a parked probe, one that leaves neither is dead.
    ///
    /// Trail-only, via <see cref="Breadcrumbs.DropToTrail"/>: at one line a second
    /// this would otherwise flush the on-screen log of everything worth reading.
    /// </summary>
    internal static class Heartbeat
    {
        /// <summary>Long enough to outlast any launch timeout worth naming.</summary>
        private const int StopAfterSeconds = 90;

        private static Thread _thread;
        private static Stopwatch _clock;
        private static string _what;

        /// <summary>
        /// Which <see cref="Start"/> the current ticker belongs to. A ticker checks it
        /// after every sleep and stops when it has been superseded. Before this it
        /// was a single stop flag, and <see cref="Start"/> reset that flag for the new
        /// ticker while the old one was still asleep — so the retry's `Start`, a
        /// millisecond after the first call's `Stop`, revived the first ticker, and
        /// `build-f295172`'s trail carries a `still inside ewk_init — +1s` from the
        /// first call in the middle of the retry.
        /// </summary>
        private static int _generation;

        /// <summary>
        /// Starts ticking once a second. <paramref name="what"/> is named in each
        /// line, in the present tense — "inside ewk_init".
        /// </summary>
        public static void Start(string what)
        {
            Start(what, 1, StopAfterSeconds);
        }

        /// <summary>
        /// Starts ticking every <paramref name="everySeconds"/>, for at most
        /// <paramref name="stopAfterSeconds"/>.
        /// </summary>
        public static void Start(string what, int everySeconds, int stopAfterSeconds)
        {
            Stop();
            _what = what;
            _clock = Stopwatch.StartNew();
            int mine = Interlocked.Increment(ref _generation);

            try
            {
                var thread = new Thread(delegate () { Tick(what, everySeconds, stopAfterSeconds, mine); });
                thread.IsBackground = true;
                thread.Name = "heartbeat";
                thread.Start();
                _thread = thread;
            }
            catch (Exception ex)
            {
                // A diagnostic is never worth failing a launch over.
                Breadcrumbs.DropToTrail("heartbeat could not start: " + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Stops ticking and records how long the call took. Safe to call when
        /// nothing is running, and safe to call twice.
        /// </summary>
        public static void Stop()
        {
            Interlocked.Increment(ref _generation);
            _thread = null;

            Stopwatch clock = _clock;
            _clock = null;

            if (clock != null)
            {
                // Timed here rather than on the ticking thread: a call that returns
                // in five milliseconds should not be reported as a second, which is
                // how long that thread would take to notice.
                Breadcrumbs.DropToTrail("returned from " + _what + " after " +
                                        clock.ElapsedMilliseconds + " ms");
            }
        }

        private static void Tick(string what, int every, int stopAfter, int mine)
        {
            for (int elapsed = every; elapsed <= stopAfter; elapsed += every)
            {
                Thread.Sleep(every * 1000);

                if (_generation != mine)
                {
                    return;
                }

                Breadcrumbs.DropToTrail("still " + what + " — +" + elapsed + "s");
            }

            if (_generation == mine)
            {
                Breadcrumbs.DropToTrail("still " + what + " after " + stopAfter +
                                        "s; no longer counting");
            }
        }
    }
}
