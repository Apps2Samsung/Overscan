using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// Runs the engine's own inspector server, which is how a click gets into a
    /// cross-origin frame on this build — see <see cref="NuiInspectorInput"/>,
    /// which is the only thing that ever asks for it.
    ///
    /// This exists because the layer below the engine turned out to be a dead end.
    /// <see cref="NuiNativeTouch"/> feeds a touch into the DALi window, which is the
    /// documented way to inject input on this platform, and on the set in issue #20
    /// it does nothing at all: both points are built, both feeds return, the release
    /// is a real 90 ms after the press, and the page sees neither a trusted event nor
    /// a frame taking focus. Nothing arrives. There is no variation on that idea left
    /// that would not be a guess at the same layer.
    ///
    /// The inspector is a different layer entirely — *inside* the engine rather than
    /// beneath it. Chromium's DevTools protocol has <c>Input.dispatchMouseEvent</c>,
    /// which the browser process injects into the widget's input pipeline: the
    /// renderer treats it as real input, so it is <c>isTrusted</c>, and the browser
    /// hit-tests it to the right renderer, which is what makes it reach a
    /// cross-origin frame. That is the same mechanism Puppeteer and Playwright use
    /// for exactly this problem, and it is the only remaining way in that does not
    /// need a privilege this app cannot have.
    ///
    /// Whether a retail set would start one at all was the open question, and issue
    /// #20 answered it: `listening on 7011`, on a 2025 TV, with the privileges the
    /// app already had, and `/json/list` fetched from a phone on the same network.
    ///
    /// That second half is also why this is <see cref="Ensure"/> and not a call at
    /// start-up. The server is unauthenticated and listens on every interface: for
    /// as long as it is up, anything else on the network can drive this browser —
    /// read the page, read its cookies, navigate it. That is a fair price for the
    /// seconds it takes to click a captcha, and no price at all worth paying for
    /// the whole of every session on the vast majority of pages, which have no
    /// frame in them to click. So it starts the first time a cross-origin frame is
    /// actually hit, and <see cref="Stop"/> takes it down again when the browser
    /// leaves the page that needed it.
    ///
    /// The symbols are P/Invoked straight out of DALi's C# binder, for the same
    /// reason and in the same way as <see cref="NuiNativeTouch"/>: TizenFX declares
    /// <c>CSharp_Dali_WebView_StartInspectorServer</c> and its <c>Stop</c> twin in
    /// its own interop layer — so the exports are there — but the managed wrappers
    /// on <c>WebView</c> are hidden, and were not in the API 9 surface this builds
    /// against.
    /// </summary>
    internal static class NuiInspector
    {
        private const string Binder = "libdali2-csharp-binder.so";

        /// <summary>
        /// Asked for first, so the reporter has a number to try before reading any
        /// diagnostics. Nothing depends on it: if the engine will not take it, the
        /// next attempt lets the engine choose.
        /// </summary>
        private const uint PreferredPort = 7011;

        /// <summary>Dali::Toolkit::WebView::StartInspectorServer(uint32_t port) — returns the port, 0 on failure.</summary>
        [DllImport(Binder, EntryPoint = "CSharp_Dali_WebView_StartInspectorServer")]
        private static extern uint StartInspectorServer(IntPtr webView, uint port);

        /// <summary>Dali::Toolkit::WebView::StopInspectorServer() — true if it took one down.</summary>
        [DllImport(Binder, EntryPoint = "CSharp_Dali_WebView_StopInspectorServer")]
        private static extern bool StopInspectorServer(IntPtr webView);

        /// <summary>What happened, for the diagnostics screen.</summary>
        public static string LastResult { get; private set; } = "(not started)";

        /// <summary>
        /// The port it actually got, or 0 if there is no server. Read by
        /// <see cref="NuiInspectorInput"/>, which is the only reason any of this
        /// exists: the number is not the point, the click that goes through it is.
        /// </summary>
        public static uint Port { get; private set; }

        /// <summary>
        /// Starts a server if there is not one already, and reports the port.
        ///
        /// Idempotent by design: the caller is the frame-click path, which asks
        /// on every cross-origin click, and starting a second server on a port
        /// already held would only turn a working one into a refusal.
        ///
        /// Best-effort, like everything else that reaches past the managed surface.
        /// A failure costs the frame click and nothing else — the browser is
        /// entirely usable without an inspector.
        /// </summary>
        public static uint Ensure(WebView web)
        {
            if (Port != 0)
            {
                return Port;
            }

            IntPtr handle = HandleOf(web);
            if (handle == IntPtr.Zero)
            {
                DiagLog.Add("inspector: " + LastResult);
                return 0;
            }

            try
            {
                uint port = StartInspectorServer(handle, PreferredPort);

                // 0 means it would not take that port. Letting the engine pick tells
                // us whether the refusal was the port or the server itself, which are
                // very different answers.
                if (port == 0)
                {
                    port = StartInspectorServer(handle, 0);
                }

                Port = port;
                LastResult = port == 0
                    ? "engine refused to start one"
                    : "listening on " + port;
            }
            catch (Exception ex)
            {
                LastResult = "start failed — " + ex.GetType().Name + ": " + ex.Message;
            }

            DiagLog.Add("inspector: " + LastResult);
            return Port;
        }

        /// <summary>
        /// Takes the server down again.
        ///
        /// Called when the browser leaves the page that needed one, because an
        /// unauthenticated debugging port on a home network is not something to
        /// leave open for the rest of an evening over a captcha that was solved
        /// ten minutes ago. If the engine will not close it the port simply stays
        /// up, which is where every build before this one already was.
        /// </summary>
        public static void Stop(WebView web)
        {
            if (Port == 0)
            {
                return;
            }

            IntPtr handle = HandleOf(web);
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                bool stopped = StopInspectorServer(handle);

                // Only a server that actually went down is forgotten. A refusal
                // leaves one listening, and forgetting it would have the next frame
                // click ask for another: the preferred port is taken, so the engine
                // would pick its own and the set would end up running two.
                if (stopped)
                {
                    Port = 0;
                }

                LastResult = stopped ? "stopped" : "would not stop — still on " + Port;
            }
            catch (Exception ex)
            {
                // Same reasoning, and this is where a firmware without the export
                // lands: the server it started is still up and still ours to use.
                LastResult = "stop failed — " + ex.GetType().Name + ": " + ex.Message;
            }

            DiagLog.Add("inspector: " + LastResult);
        }

        /// <summary>
        /// The web view's native pointer. Same reflection as
        /// <see cref="NuiNativeTouch.HandleOf"/> and for the same reason:
        /// <c>SwigCPtr</c> is the handle TizenFX itself hands to this binder, and
        /// reading it is exactly as correct as the call it feeds.
        /// </summary>
        private static IntPtr HandleOf(WebView web)
        {
            if (web == null)
            {
                LastResult = "no web view";
                return IntPtr.Zero;
            }

            try
            {
                for (Type type = web.GetType(); type != null; type = type.BaseType)
                {
                    PropertyInfo property = type.GetProperty(
                        "SwigCPtr",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (property != null && property.CanRead &&
                        property.PropertyType == typeof(HandleRef))
                    {
                        return ((HandleRef)property.GetValue(web)).Handle;
                    }
                }

                LastResult = "web view handle not reachable";
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                LastResult = "web view handle unreadable — " + ex.GetType().Name + ": " + ex.Message;
                return IntPtr.Zero;
            }
        }
    }
}
