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

            // Before anything else can end the process. The trail from issue #20's
            // last build ends on a memory reading with the app in perfect health a
            // few seconds earlier, and none of the three lines below this point were
            // written — so the run neither returned from the loop nor threw out of
            // it. This is what makes the difference between the platform closing
            // this app and the engine crashing it readable on the next launch.
            NuiDeathWatch.Arm();

            // And the engine's own last words. NativeStdErr has always redirected
            // stdout/stderr around a single call; this holds the redirection open
            // for the run, which is the only way a *child* process — chromium's
            // renderer, the suspect on issue #20 — gets to leave anything behind on
            // a TV with no dlog reader. Deliberately after Breadcrumbs.Init, which
            // is what moves the last run's copy aside to be read.
            if (NativeStdErr.StartSession())
            {
                Breadcrumbs.DropToTrail("capturing native output for this run");
            }
            else
            {
                Breadcrumbs.DropToTrail("stderr: " + NativeStdErr.SessionState);
            }

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
