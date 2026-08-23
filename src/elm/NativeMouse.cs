using System;
using System.Runtime.InteropServices;
using ElmSharp;

namespace Overscan
{
    /// <summary>
    /// Feeds a real mouse click into the Evas canvas, for the one case the injected
    /// page script cannot reach: a cross-origin <c>&lt;iframe&gt;</c>.
    ///
    /// The pointer is normally implemented inside the page (see
    /// <see cref="PageScript"/>) because chromium-efl exposes no API for synthetic
    /// input. That works everywhere except across an origin boundary: a captcha, an
    /// embedded sign-in or a payment widget lives in an iframe served from another
    /// host, so <c>elementFromPoint</c> returns the iframe element itself and a
    /// click dispatched on it does nothing at all — which is what issue #15 hit on
    /// Instagram's captcha.
    ///
    /// EFL does have an input path an app may drive: <c>evas_event_feed_mouse_*</c>
    /// pushes an event through the canvas's own hit-testing, so the web view
    /// receives it exactly as it receives the TV's pointer, and chromium routes it
    /// into whichever frame is under the point. The events are trusted, so a
    /// captcha accepts them.
    ///
    /// Why this is not simply used for every click: a real click on a text field
    /// focuses it, and a focused field makes the TV raise its platform IME, which
    /// then swallows the remote — the exact failure the in-page pointer exists to
    /// avoid. So this stays a fallback for frames, not the default.
    ///
    /// Everything is best-effort. If the EFL sonames differ on some firmware, or a
    /// symbol will not bind, the call is logged and the click behaves as it did
    /// before.
    /// </summary>
    internal static class NativeMouse
    {
        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        /// <summary>EVAS_BUTTON_NONE: not a double or triple click.</summary>
        private const int ButtonFlagsNone = 0;

        private const int LeftButton = 1;

        private static bool _probed;
        private static bool _usable;
        private static bool _entered;
        private static uint _stamp;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libevas.so.1")]
        private static extern IntPtr evas_object_evas_get(IntPtr obj);

        [DllImport("libevas.so.1")]
        private static extern void evas_event_feed_mouse_in(IntPtr e, uint timestamp, IntPtr data);

        [DllImport("libevas.so.1")]
        private static extern void evas_event_feed_mouse_move(IntPtr e, int x, int y, uint timestamp, IntPtr data);

        [DllImport("libevas.so.1")]
        private static extern void evas_event_feed_mouse_down(IntPtr e, int button, int flags, uint timestamp, IntPtr data);

        [DllImport("libevas.so.1")]
        private static extern void evas_event_feed_mouse_up(IntPtr e, int button, int flags, uint timestamp, IntPtr data);

        /// <summary>Why the last attempt did nothing, for the diagnostics screen.</summary>
        public static string LastResult { get; private set; } = "(not used yet)";

        /// <summary>
        /// Clicks at a point in canvas coordinates. Returns false when the platform
        /// would not let us, in which case nothing happened at all.
        /// </summary>
        public static bool Click(EvasObject target, int x, int y)
        {
            IntPtr canvas = CanvasOf(target);
            if (canvas == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // A canvas that has never seen the pointer enter it discards moves,
                // so the first click has to announce the pointer's arrival.
                if (!_entered)
                {
                    evas_event_feed_mouse_in(canvas, NextStamp(), IntPtr.Zero);
                    _entered = true;
                }

                // Move first: Evas decides which object is under the pointer from
                // the move, and the press carries no coordinates of its own.
                evas_event_feed_mouse_move(canvas, x, y, NextStamp(), IntPtr.Zero);
                evas_event_feed_mouse_down(canvas, LeftButton, ButtonFlagsNone, NextStamp(), IntPtr.Zero);
                evas_event_feed_mouse_up(canvas, LeftButton, ButtonFlagsNone, NextStamp(), IntPtr.Zero);

                LastResult = "fed click at " + x + "," + y;
                DiagLog.Add("native mouse: " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                _usable = false;
                LastResult = "feed failed — " + ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("native mouse: " + LastResult);
                return false;
            }
        }

        /// <summary>
        /// The Evas the object is drawn on. Probed once: on a platform where the
        /// symbols do not bind there is no point retrying on every click.
        /// </summary>
        private static IntPtr CanvasOf(EvasObject target)
        {
            if (target == null)
            {
                LastResult = "no view";
                return IntPtr.Zero;
            }

            if (_probed && !_usable)
            {
                return IntPtr.Zero;
            }

            IntPtr handle = target.RealHandle != IntPtr.Zero ? target.RealHandle : target.Handle;
            if (handle == IntPtr.Zero)
            {
                LastResult = "view has no native handle";
                return IntPtr.Zero;
            }

            try
            {
                if (!_probed)
                {
                    _probed = true;

                    // Same reason as NativeEngine.Preload: on these TVs the .NET
                    // loader has been seen to mangle a "libX.so.N" name past
                    // recognition, and dlopen by absolute path with RTLD_GLOBAL
                    // makes the later P/Invoke bind to the already-loaded soname.
                    dlopen("/usr/lib/libevas.so.1", RtldNow | RtldGlobal);
                }

                IntPtr canvas = evas_object_evas_get(handle);
                _usable = canvas != IntPtr.Zero;
                if (!_usable)
                {
                    LastResult = "evas_object_evas_get returned null";
                    DiagLog.Add("native mouse: " + LastResult);
                }

                return canvas;
            }
            catch (Exception ex)
            {
                _usable = false;
                LastResult = "libevas unavailable — " + ex.GetType().Name;
                DiagLog.Add("native mouse: " + LastResult);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Evas reads timestamps for double-click detection, so they have to keep
        /// increasing and be far enough apart not to look like one.
        /// </summary>
        private static uint NextStamp()
        {
            uint now = unchecked((uint)Environment.TickCount);
            _stamp = now > _stamp ? now : _stamp + 1;
            return _stamp;
        }
    }
}
