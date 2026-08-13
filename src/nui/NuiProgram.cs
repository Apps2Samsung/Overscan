using System;
using System.Threading;

namespace Overscan
{
    internal static class NuiProgram
    {
        private static void Main(string[] args)
        {
            // Same as the ElmSharp build: the diag socket comes up before anything
            // that can fail, because a locked-down TV has no other channel.
            DiagServer.Start();
            DiagLog.Add("Main entered (NUI build)");

            try
            {
                new NuiBrowserApp().Run(args);
                DiagLog.Add("app loop returned");
            }
            catch (Exception ex)
            {
                DiagLog.Add("FATAL in Main: " + ex.GetType().Name + ": " + ex.Message);
                DiagLog.Add("stack: " + ex.StackTrace);
                DiagLog.Add("holding process open for diagnostics");
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }
}
