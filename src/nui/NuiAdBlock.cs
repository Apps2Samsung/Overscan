using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// Ad blocking on the 2025+ build, done by refusing the request before it
    /// leaves the TV. Issue #37.
    /// </summary>
    /// <remarks>
    /// The NUI WebView's <c>WebContext</c> can hand every HTTP request to the app
    /// before the engine sends it, and the app can answer it instead. That is
    /// real blocking: no bytes, no connection, no script run. The four
    /// <c>Tizen.WebView</c> packages have no such hook, which is why this file is
    /// under <c>src/nui</c> and the older sets get nothing rather than a
    /// look-alike that hides boxes after the page has already paid for them.
    ///
    /// Three things about the callback decide the shape of this class, and all
    /// three are in the toolkit's own documentation of it:
    ///
    /// <list type="bullet">
    /// <item>It runs on a thread that is not the UI thread. So nothing here
    /// touches a view, the log, or anything else the main thread owns. The
    /// counters are <see cref="Interlocked"/>, the switch is <c>volatile</c>, and
    /// the list is built once and never written again.</item>
    /// <item>A request that is neither ignored nor answered is a request that
    /// never completes. So every path out of <see cref="OnRequest"/> does one or
    /// the other, and the whole body is under a catch that ignores the request
    /// rather than let an exception of ours cross into the engine.</item>
    /// <item>It is in the path of every request the engine makes, on a TV. So the
    /// time spent in it is measured from the first build and printed in the
    /// report, because "pages feel slower" is exactly the complaint this feature
    /// exists to prevent, and without a number nobody could tell whether the
    /// feature or the site was the cause.</item>
    /// </list>
    ///
    /// A refused request gets a 403 with an empty body rather than a hang or a
    /// connection error. The page sees an ordinary failed load, which is the case
    /// every site already handles; a stalled request is the case none of them do.
    /// </remarks>
    internal static class NuiAdBlock
    {
        private static readonly byte[] EmptyBody = new byte[0];

        private static AdHosts _hosts;
        private static volatile bool _enabled;

        // Held so the delegate cannot be collected out from under the native side;
        // the toolkit keeps its own reference today, and this costs nothing.
        private static WebContext.HttpRequestInterceptedCallback _callback;

        private static long _seen;
        private static long _refused;
        private static long _failed;
        private static long _ticks;
        private static long _maxTicks;
        private static string _lastRefused = "(none yet)";

        /// <summary>How installation went, for the report. Never throws.</summary>
        public static string LastResult = "(not installed)";

        /// <summary>The switch behind the menu row. Read on the request thread.</summary>
        public static bool Enabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        /// <summary>
        /// Registers the interceptor. Best-effort like everything that reaches past
        /// managed code: a failure is recorded in <see cref="LastResult"/> and the
        /// browser carries on without blocking.
        /// </summary>
        public static void Install(WebView web, bool enabled)
        {
            _enabled = enabled;
            try
            {
                _hosts = AdHosts.LoadEmbedded();

                WebContext context = web == null ? null : web.Context;
                if (context == null)
                {
                    LastResult = "engine offered no context";
                    return;
                }

                _callback = OnRequest;
                context.RegisterHttpRequestInterceptedCallback(_callback);
                LastResult = "installed, " + _hosts.Count + " hosts";
            }
            catch (Exception ex)
            {
                LastResult = "install failed: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static void OnRequest(WebHttpRequestInterceptor request)
        {
            long started = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _seen);

            try
            {
                string host = null;
                bool refuse = false;
                if (_enabled && _hosts != null)
                {
                    host = AdHosts.HostOf(request.Url);
                    refuse = _hosts.Matches(host);
                }

                if (!refuse)
                {
                    request.Ignore();
                }
                else
                {
                    Interlocked.Increment(ref _refused);
                    _lastRefused = host;

                    // Status, then body. The body call is the last thing allowed on
                    // the object; if the status call fails the request is still
                    // ours to hand back, so it is ignored rather than left hanging.
                    if (request.SetResponseStatus(403, "Refused by Overscan"))
                    {
                        request.AddResponseHeader("Content-Type", "text/plain");
                        if (!request.SetResponseBody(EmptyBody))
                        {
                            Interlocked.Increment(ref _failed);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _failed);
                        request.Ignore();
                    }
                }
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _failed);
                try
                {
                    request.Ignore();
                }
                catch (Exception)
                {
                    // Nothing left to do for this request; the counter has it.
                }
            }

            long spent = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _ticks, spent);
            long max;
            do
            {
                max = Interlocked.Read(ref _maxTicks);
                if (spent <= max)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _maxTicks, spent, max) != max);
        }

        /// <summary>One line for the report: state, counts, and the handler's cost.</summary>
        public static string Summary()
        {
            long seen = Interlocked.Read(ref _seen);
            long refused = Interlocked.Read(ref _refused);
            long failed = Interlocked.Read(ref _failed);
            long ticks = Interlocked.Read(ref _ticks);
            long maxTicks = Interlocked.Read(ref _maxTicks);

            string timing;
            if (seen == 0)
            {
                timing = "no requests yet";
            }
            else
            {
                double msPerTick = 1000.0 / Stopwatch.Frequency;
                timing = string.Format(CultureInfo.InvariantCulture,
                    "handler {0:0.000} ms avg, {1:0.00} ms max",
                    ticks * msPerTick / seen, maxTicks * msPerTick);
            }

            return (_enabled ? "on" : "off") + ", " + LastResult +
                   " | " + seen + " requests, " + refused + " refused" +
                   (failed > 0 ? ", " + failed + " could not be answered" : string.Empty) +
                   " | " + timing + " | last refused: " + _lastRefused;
        }
    }
}
