using System;
using System.Threading;
using ElmSharp;

namespace Overscan
{
    internal static class Program
    {
        private const string PackageId = "org.apps2samsung.overscan";

        private static void Main(string[] args)
        {
            // First statement in the process: on a locked-down TV this socket is
            // the only way to find out anything at all (see DiagServer).
            DiagServer.Start();

            // Breadcrumbs before anything else that can die natively: a crash that
            // takes the process down leaves no log and no socket, and the last line
            // on disk is then the only thing that says where it happened. The
            // browser used to leave this to OverscanProbe, which meant the two
            // "installs, launches, disappears" reports (#13, #14) had nothing to
            // read but a log that stopped mid-start-up.
            Breadcrumbs.Init(PackageId);
            Breadcrumbs.Drop("Main entered");

            try
            {
                // Required before any ElmSharp widget is created (the Tizen.WebView
                // sample in TizenFX does the same). ElmSharp is deprecated from API
                // 10, so on a newer platform this is a place that can fail.
                Elementary.Initialize();
                Elementary.ThemeOverlay();
                Breadcrumbs.Drop("Elementary initialized");

                new BrowserApp().Run(args);
                Breadcrumbs.Drop("app loop returned");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("FATAL in Main: " + ex.GetType().Name + ": " + ex.Message);
                DiagLog.Add("stack: " + ex.StackTrace);

                // Stay alive so the report can still be read over :8081. Without
                // this the process exits and takes the only diagnostic channel
                // with it.
                DiagLog.Add("holding process open for diagnostics");
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
