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
        private const int Capacity = 12;
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
            lock (Sync)
            {
                var sb = new StringBuilder();
                foreach (string line in Lines)
                {
                    sb.Append(line).Append('\n');
                }

                return sb.ToString();
            }
        }
    }
}
