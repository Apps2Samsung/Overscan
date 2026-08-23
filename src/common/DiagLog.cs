using System;
using System.Collections.Generic;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// On-screen log ring buffer. dlog is the normal channel, but a retail TV with
    /// intershell disabled gives no `sdb shell dlogutil`, so everything we need to
    /// see during a device test has to be renderable on the TV itself.
    /// </summary>
    internal static class DiagLog
    {
        // Start-up alone now drops a dozen breadcrumbs, and the interesting part of
        // a failed launch is the *beginning* of it: at 12 lines the first pages of
        // a report evicted exactly the lines that said where the app got to.
        private const int Capacity = 60;
        private static readonly Queue<string> Lines = new Queue<string>();
        private static readonly object Sync = new object();

        /// <summary>dlog tag. Unreachable on a locked-down TV, hence DiagServer.</summary>
        public const string LogTag = "Overscan";

        public static void Add(string message)
        {
            Tizen.Log.Info(LogTag, message);

            lock (Sync)
            {
                Lines.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
                while (Lines.Count > Capacity)
                {
                    Lines.Dequeue();
                }
            }
        }

        public static string Dump()
        {
            return Tail(Capacity);
        }

        /// <summary>
        /// The last <paramref name="count"/> lines. The on-screen overlay has a
        /// fixed box to draw in, so it asks for what fits rather than everything.
        /// </summary>
        public static string Tail(int count)
        {
            lock (Sync)
            {
                int skip = Lines.Count - count;
                var sb = new StringBuilder();
                int index = 0;
                foreach (string line in Lines)
                {
                    if (index++ < skip)
                    {
                        continue;
                    }

                    sb.Append(line).Append('\n');
                }

                return sb.ToString();
            }
        }
    }
}
