using System;
using System.Collections.Generic;

// The one surface EngineImports reaches for besides the loader: the trail. Kept in
// memory here and read back by the assertions.
namespace Overscan
{
    internal static class Breadcrumbs
    {
        private static readonly List<string> Written = new List<string>();

        private static readonly object Gate = new object();

        public static void Drop(string message)
        {
            lock (Gate)
            {
                Written.Add(message);
            }

            Console.WriteLine("    | " + message);
        }

        public static string Trail
        {
            get
            {
                lock (Gate)
                {
                    return string.Join("\n", Written.ToArray());
                }
            }
        }
    }
}
