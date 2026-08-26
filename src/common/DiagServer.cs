using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Serves <see cref="DiagLog"/> over plain HTTP on <see cref="Port"/>.
    ///
    /// A locked-down TV (and the Tizen 10.0 TV emulator, which reports
    /// intershell_support:disabled) gives no `sdb shell`, no `dlogutil`, and
    /// refuses `sdb pull` outside the sdk_tools path — so when the app fails
    /// before it can draw anything, there is no way to find out why. `sdb forward`
    /// still works, so a socket is the one channel left:
    ///
    ///     sdb forward tcp:8081 tcp:8081
    ///     curl http://localhost:8081
    ///
    /// It is started as the very first statement in Main, before anything that
    /// could throw, so "nothing is listening" is itself a result: it means the
    /// process never got as far as running managed code.
    /// </summary>
    internal static class DiagServer
    {
        public const int Port = 8081;

        private static Thread _thread;

        /// <summary>Set by the app so the report can include live state.</summary>
        public static Func<string> ReportProvider;

        public static void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        private static void Loop()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                DiagLog.Add("diag server listening on :" + Port);

                while (true)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        try
                        {
                            // Drain the request; the server has exactly one page.
                            stream.ReadTimeout = 2000;
                            var scratch = new byte[2048];
                            stream.Read(scratch, 0, scratch.Length);
                        }
                        catch (Exception)
                        {
                            // A client that sends nothing still gets the report.
                        }

                        byte[] payload = Encoding.UTF8.GetBytes(Report());
                        byte[] head = Encoding.UTF8.GetBytes(
                            "HTTP/1.0 200 OK\r\n" +
                            "Content-Type: text/plain; charset=utf-8\r\n" +
                            "Content-Length: " + payload.Length + "\r\n" +
                            "Connection: close\r\n\r\n");

                        stream.Write(head, 0, head.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("diag server died: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Report()
        {
            Func<string> provider = ReportProvider;
            if (provider == null)
            {
                // The previous run's evidence does not need the UI to exist, and
                // this page is what a reporter gets when they are quick off the
                // mark — issue #17's first fetch showed three lines and nothing
                // else. It is the same trail the full report carries.
                return "Overscan — no report provider yet (UI has not started)\n\n" +
                       "this run\n" + DiagLog.Dump() +
                       "\nprevious run (last line is where it died)\n" +
                       Breadcrumbs.Previous +
                       "\nengine stdout/stderr (previous run)\n" +
                       Breadcrumbs.PreviousStdErr + "\n";
            }

            try
            {
                return provider();
            }
            catch (Exception ex)
            {
                return "report provider threw " + ex.GetType().Name + ": " + ex.Message + "\n\n" +
                       DiagLog.Dump();
            }
        }
    }
}
