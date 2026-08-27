using System;
using System.Threading;

namespace Overscan
{
    internal static class NuiProgram
    {
        private const string PackageId = "org.apps2samsung.overscan";

        private static void Main(string[] args)
        {
            // Same as the ElmSharp build: the diag socket comes up before anything
            // that can fail, because a locked-down TV has no other channel.
            DiagServer.Start();

            // And a trail on disk, for the same reason the ElmSharp build has had
            // one since #13: a native crash takes the process down with the log
            // inside it, so the only account of where it happened is whatever
            // reached disk first. This build now has a report of exactly that
            // shape — Overscan dies on an Instagram reel (issue #20) and nothing
            // on the TV says where — and until now the NUI half left nothing to
            // read afterwards at all.
            Breadcrumbs.Init(PackageId);
            Breadcrumbs.Drop("Main entered (NUI build)");

            try
            {
                new NuiBrowserApp().Run(args);
                Breadcrumbs.Drop("app loop returned");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("FATAL in Main: " + ex.GetType().Name + ": " + ex.Message);
                DiagLog.Add("stack: " + ex.StackTrace);
                DiagLog.Add("holding process open for diagnostics");
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
