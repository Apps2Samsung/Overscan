using System;
using System.Globalization;
using System.IO;

namespace Overscan
{
    /// <summary>
    /// How much memory this process is holding, read from <c>/proc/self/statm</c>.
    ///
    /// It exists to separate two deaths that look identical from the sofa. A page
    /// that kills the app can either crash it — the engine falling over inside a
    /// native call, which stops the process where it stands — or grow it until
    /// Tizen's low-memory killer takes it away. Both leave the same "the app just
    /// closed" report, and they need opposite fixes.
    ///
    /// On the trail those two look nothing alike: a crash ends on whatever call
    /// was in flight, while an eviction ends on a memory line noticeably larger
    /// than the ones before it. That is the whole point of writing this number
    /// down every few seconds rather than only when something goes wrong — by the
    /// time it goes wrong there is nobody left to ask.
    /// </summary>
    internal static class ProcessMemory
    {
        /// <summary>Linux reports statm in pages; every platform we run on is 4K.</summary>
        private const long PageSize = 4096;

        /// <summary>
        /// Resident size in megabytes, or -1 when it cannot be read. Never throws:
        /// this is a diagnostic and a diagnostic may not be the thing that ends a
        /// browsing session.
        /// </summary>
        public static long ResidentMb()
        {
            try
            {
                // "size resident shared text lib data dt" — the second field.
                string[] fields = File.ReadAllText("/proc/self/statm")
                    .Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                long pages;
                if (fields.Length < 2 ||
                    !long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out pages))
                {
                    return -1;
                }

                return pages * PageSize / (1024 * 1024);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>The same number as a line for the trail or the report.</summary>
        public static string Summary()
        {
            long mb = ResidentMb();
            return mb < 0
                ? "(unreadable)"
                : mb.ToString(CultureInfo.InvariantCulture) + " MB resident";
        }
    }
}
