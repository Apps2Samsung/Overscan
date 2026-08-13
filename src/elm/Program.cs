using System;
using System.Threading;
using ElmSharp;

namespace Overscan
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // First statement in the process: on a locked-down TV this socket is
            // the only way to find out anything at all (see DiagServer).
            DiagServer.Start();
            DiagLog.Add("Main entered");

            try
            {
                // Required before any ElmSharp widget is created (the Tizen.WebView
                // sample in TizenFX does the same). ElmSharp is deprecated from API
                // 10, so on a newer platform this is a place that can fail.
                Elementary.Initialize();
                Elementary.ThemeOverlay();
                DiagLog.Add("Elementary initialized");

                new BrowserApp().Run(args);
                DiagLog.Add("app loop returned");
            }
            catch (Exception ex)
            {
                DiagLog.Add("FATAL in Main: " + ex.GetType().Name + ": " + ex.Message);
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
