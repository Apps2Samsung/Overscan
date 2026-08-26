using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// Starts the engine's own remote inspector, and reports the port it got.
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
    /// Two things have to be true before any of that is worth building, and both are
    /// unknown on a retail set: that the server starts at all, and that it is
    /// reachable. So this is only the question, not the answer — start it, report the
    /// port, and let the reporter try to open it. A CDP client is a fair amount of
    /// code (a WebSocket client, for a start) and there is no sense writing it blind
    /// a fourth time.
    ///
    /// The symbol is P/Invoked straight out of DALi's C# binder, for the same reason
    /// and in the same way as <see cref="NuiNativeTouch"/>: TizenFX declares
    /// <c>CSharp_Dali_WebView_StartInspectorServer</c> in its own interop layer at
    /// API 9 — so the export is there — but exposes no managed wrapper for it.
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

        /// <summary>What happened, for the diagnostics screen.</summary>
        public static string LastResult { get; private set; } = "(not started)";

        /// <summary>
        /// Best-effort, like everything else that reaches past the managed surface.
        /// A failure here costs nothing — the browser is entirely usable without an
        /// inspector, and the only thing lost is the answer to the question above.
        /// </summary>
        public static void Start(WebView web)
        {
            IntPtr handle = HandleOf(web);
            if (handle == IntPtr.Zero)
            {
                DiagLog.Add("inspector: " + LastResult);
                return;
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

                LastResult = port == 0
                    ? "engine refused to start one"
                    : "listening on " + port;
            }
            catch (Exception ex)
            {
                LastResult = "start failed — " + ex.GetType().Name + ": " + ex.Message;
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
