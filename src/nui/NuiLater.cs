using System;
using System.Collections.Generic;
using Tizen.NUI;

namespace Overscan
{
    /// <summary>
    /// Runs something once, a moment from now, on the main thread.
    ///
    /// DALi may only be touched from the thread that owns it, so the usual answers
    /// — a worker thread, a <c>System.Threading.Timer</c> — are not answers here:
    /// they would deliver the callback on the wrong thread and the crash would land
    /// somewhere unrelated. NUI's own <see cref="Timer"/> ticks on the main loop,
    /// which is the whole reason to prefer it.
    ///
    /// The list is not an optimisation. A NUI Timer with no live reference can be
    /// collected between <c>Start</c> and its first tick and simply never fire, and
    /// the two callers here both use it for the second half of something — the
    /// release of a touch, the reading of a result — so a tick that goes missing
    /// leaves a contact down or a question unanswered rather than merely being late.
    /// </summary>
    internal static class NuiLater
    {
        private static readonly List<Timer> Armed = new List<Timer>();

        /// <summary>
        /// Calls <paramref name="action"/> once, after <paramref name="milliseconds"/>.
        /// Returns whether the wait was actually armed — the callers here both have
        /// something to undo if it was not, so "it will happen later" is not a claim
        /// to make on faith. Never throws: the callback's own exceptions are logged,
        /// because a timer tick has no caller to hand them to.
        /// </summary>
        public static bool Once(int milliseconds, Action action)
        {
            if (action == null)
            {
                return false;
            }

            try
            {
                var timer = new Timer((uint)Math.Max(1, milliseconds));
                Armed.Add(timer);

                timer.Tick += delegate (object sender, Timer.TickEventArgs e)
                {
                    Armed.Remove(timer);

                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        DiagLog.Add("deferred call failed: " + ex.Message);
                    }

                    // One shot.
                    return false;
                };

                timer.Start();
                return true;
            }
            catch (Exception ex)
            {
                // No timer means no delay. Running it now is wrong for anything that
                // needed the wait, so say so and let the caller decide.
                DiagLog.Add("could not defer by " + milliseconds + "ms: " + ex.Message);
                return false;
            }
        }
    }
}
