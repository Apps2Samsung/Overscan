using System.Threading;

// The one platform surface the trail reaches for: dlog. It is a stand-in that can
// be told to block, because on issue #17's set a write to a socket some other
// process may or may not be reading is exactly as trustworthy as a write to a file.
namespace Tizen
{
    internal static class Log
    {
        /// <summary>Set to hold every Info call until <see cref="Release"/>.</summary>
        public static readonly ManualResetEvent Gate = new ManualResetEvent(true);

        public static int Calls;

        public static void Info(string tag, string message)
        {
            Interlocked.Increment(ref Calls);
            Gate.WaitOne();
        }
    }
}
