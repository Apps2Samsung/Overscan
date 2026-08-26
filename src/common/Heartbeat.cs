using System;
using System.Diagnostics;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Times a native call that might not come back.
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
    /// Trail-only, via <see cref="Breadcrumbs.DropToTrail"/>: at one line a second
    /// this would otherwise flush the on-screen log of everything worth reading.
    /// </summary>
    internal static class Heartbeat
    {
        /// <summary>Long enough to outlast any launch timeout worth naming.</summary>
        private const int StopAfterSeconds = 90;

        private static Thread _thread;
        private static volatile bool _stop;
        private static Stopwatch _clock;
        private static string _what;

        /// <summary>
        /// Starts ticking. <paramref name="what"/> is named in each line, in the
        /// present tense — "inside ewk_init".
        /// </summary>
        public static void Start(string what)
        {
            Stop();
            _stop = false;
            _what = what;
            _clock = Stopwatch.StartNew();

            try
            {
                var thread = new Thread(delegate () { Tick(what); });
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
            _stop = true;
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

        private static void Tick(string what)
        {
            for (int second = 1; second <= StopAfterSeconds; second++)
            {
                Thread.Sleep(1000);

                if (_stop)
                {
                    return;
                }

                Breadcrumbs.DropToTrail("still " + what + " — +" + second + "s");
            }

            Breadcrumbs.DropToTrail("still " + what + " after " + StopAfterSeconds +
                                    "s; no longer counting");
        }
    }
}
