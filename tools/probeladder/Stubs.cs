using System;
using System.Collections.Generic;

// The three platform surfaces NativeProbe reaches for, and nothing else. They are
// stand-ins on purpose: the subject of this harness is the ladder — which rungs get
// asked, in what order, and what happens to the ones behind a rung that never
// answers — and none of that involves Smack, a TV, or an app framework.
//
// The signatures are not stand-ins. A change to any of them on the real classes
// breaks this build, which is the cheapest possible warning that the probe has
// grown a dependency this harness no longer covers.
namespace Tizen.Applications
{
    internal sealed class DirectoryInfo
    {
        public string Resource { get; set; }

        public string Data { get; set; }
    }

    internal class Application
    {
        public static Application Current
        {
            get { return Layout == null ? null : Instance; }
        }

        /// <summary>The layout the harness put on disk for this scenario.</summary>
        public static DirectoryInfo Layout;

        private static readonly Application Instance = new Application();

        public DirectoryInfo DirectoryInfo
        {
            get { return Layout; }
        }
    }
}

namespace Overscan
{
    /// <summary>
    /// The trail, kept in memory. The real one writes each line to disk on its own so
    /// it outlives a crash; here it is read back by the assertions instead.
    /// </summary>
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

    /// <summary>
    /// Stands in for the permission investigation. NativeProbe only reads these two
    /// for the record, after the verdict, and both are ledgered — so what the harness
    /// needs from them is that they are called at all, not what they say.
    /// </summary>
    internal static class SmackWall
    {
        public static string MountOf(string path)
        {
            return "(harness: no mount table)";
        }

        public static string Xattr(string path, string label)
        {
            return null;
        }
    }
}
