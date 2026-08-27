using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Stand-in for the app's log, so the file under test compiles unchanged.
    /// </summary>
    internal static class DiagLog
    {
        private static readonly List<string> Lines = new List<string>();

        public static void Add(string message)
        {
            lock (Lines)
            {
                Lines.Add(message);
            }

            Console.WriteLine("    log | " + message);
        }
    }

    /// <summary>
    /// Stand-in for the inspector server, which on a TV is chromium's own and here
    /// is whatever port the harness was pointed at.
    /// </summary>
    internal static class NuiInspector
    {
        public static uint Port { get; set; }
    }

    internal static class Harness
    {
        /// <summary>
        /// Usage: cdpharness &lt;port&gt; [&lt;x&gt; &lt;y&gt; ...]
        ///
        /// With points on the command line it clicks each in turn. With none it
        /// reads "x y" lines from standard input instead, which is how the
        /// interesting cases are driven: a target closed and reopened between two
        /// clicks, a server stopped and started again, one process living across
        /// all of it exactly as the app does.
        /// </summary>
        private static int Main(string[] args)
        {
            if (args.Length < 1 || (args.Length - 1) % 2 != 0)
            {
                Console.Error.WriteLine("usage: cdpharness <port> [<x> <y> ...]");
                return 2;
            }

            NuiInspector.Port = uint.Parse(args[0], CultureInfo.InvariantCulture);

            bool ok = true;

            if (args.Length > 1)
            {
                for (int i = 1; i + 1 < args.Length; i += 2)
                {
                    ok &= Click(int.Parse(args[i], CultureInfo.InvariantCulture),
                                int.Parse(args[i + 1], CultureInfo.InvariantCulture));
                }

                return ok ? 0 : 1;
            }

            string line;
            while ((line = Console.ReadLine()) != null)
            {
                string[] parts = line.Trim().Split(' ');
                if (parts.Length != 2)
                {
                    continue;
                }

                ok &= Click(int.Parse(parts[0], CultureInfo.InvariantCulture),
                            int.Parse(parts[1], CultureInfo.InvariantCulture));
            }

            return ok ? 0 : 1;
        }

        private static bool Click(int x, int y)
        {
            Console.WriteLine("--> click " + x + "," + y);

            if (!NuiInspectorInput.Click(x, y))
            {
                Console.WriteLine("    refused: " + NuiInspectorInput.LastResult);
                return false;
            }

            // The click is asynchronous by design; the app waits by asking the page
            // a moment later, and so does this.
            Thread.Sleep(2000);
            Console.WriteLine("    result : " + NuiInspectorInput.LastResult +
                              "  (ok=" + NuiInspectorInput.Succeeded + ")");
            return NuiInspectorInput.Succeeded;
        }
    }
}
