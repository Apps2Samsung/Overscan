using System;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// One call that might not come back, made on a thread of its own, with a
    /// deadline. The answer is the call's own string, or <see cref="Missed"/>.
    ///
    /// Issue #17's set does not refuse the calls it objects to; it parks them. In
    /// the kernel, for good — `getxattr` twice, an `open` of our own file in `res/`,
    /// and on `build-f295172` a `Directory.GetFiles` on the engine's directory and
    /// the probe ledger's own file I/O, on two consecutive launches, on the two
    /// threads that mattered. A call like that leaves the app alive and the trail
    /// silent, which from a report is indistinguishable from a call that was never
    /// made. So every call on the failure path that reaches past managed code goes
    /// out through here, and a miss is a recorded answer rather than a silence.
    ///
    /// A call that misses is left running and written off. Nothing else is possible:
    /// .NET Core has no <c>Thread.Abort</c>, and a thread stopped inside a syscall
    /// would not honour one anyway. The thread is a background thread so a call
    /// still stuck cannot hold the process open after the user has walked away, and
    /// the event it signals is deliberately never disposed — disposing it under a
    /// thread that is still holding it would raise on a thread with nobody to catch
    /// it, which on this app means the crash this class exists to avoid.
    ///
    /// This used to be a private method of <c>NativeProbe.Ledger</c>. It moved here
    /// when it turned out the ledger's own <c>open</c> needed it, and so did the
    /// engine explainer on the main thread: the same shape, twice more, in the two
    /// places that had been called safe because nothing had ever refused them.
    /// </summary>
    internal static class Deadline
    {
        /// <summary>
        /// What a call's answer reads as when it was still inside the kernel when the
        /// deadline passed. Nothing about it says the request was refused, only that
        /// this firmware will not finish answering it.
        /// </summary>
        public const string Missed = "DID NOT RETURN";

        /// <summary>
        /// How long any one diagnostic call gets. Every call made through here is a
        /// syscall against a small file or a directory of one entry: if it has not
        /// come back in five seconds it is not coming back, and the worst case —
        /// every rung of every location hanging — still finishes the probe's walk
        /// inside three minutes, on a thread nobody is waiting on.
        /// </summary>
        public const int DefaultMs = 5000;

        public static string Run(Func<string> call)
        {
            return Run(call, DefaultMs);
        }

        /// <summary>
        /// Makes <paramref name="call"/> under a deadline of <paramref name="timeoutMs"/>.
        /// An exception out of the call comes back as text rather than propagating —
        /// a diagnostic has nobody to throw to.
        /// </summary>
        public static string Run(Func<string> call, int timeoutMs)
        {
            string answer = null;
            Exception failure = null;
            var finished = new ManualResetEvent(false);

            try
            {
                var thread = new Thread(delegate ()
                {
                    try
                    {
                        answer = call();
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    try
                    {
                        finished.Set();
                    }
                    catch (Exception)
                    {
                        // Nobody to report it to: the waiter has already given up.
                    }
                });

                thread.IsBackground = true;
                thread.Name = "deadline";
                thread.Start();

                if (!finished.WaitOne(timeoutMs))
                {
                    return Missed;
                }
            }
            catch (Exception ex)
            {
                // A set that will not give us a thread still gets asked, it just gets
                // asked the dangerous way — which is what every build before the
                // watchdog did on every call.
                try
                {
                    return call();
                }
                catch (Exception inner)
                {
                    return "threw " + inner.GetType().Name + ": " + inner.Message +
                           " (no thread for a deadline: " + ex.GetType().Name + ")";
                }
            }

            if (failure != null)
            {
                return "threw " + failure.GetType().Name + ": " + failure.Message;
            }

            return answer ?? "(the call returned nothing)";
        }
    }
}
