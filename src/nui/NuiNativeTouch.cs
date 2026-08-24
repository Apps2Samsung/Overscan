using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Tizen.NUI;

namespace Overscan
{
    /// <summary>
    /// Feeds a real touch into the DALi window, for the one case the injected page
    /// script cannot reach: a cross-origin <c>&lt;iframe&gt;</c>.
    ///
    /// This is the NUI counterpart of <see cref="NativeMouse"/>, and it exists for
    /// the same reason. The pointer is drawn and hit-tested inside the page (see
    /// <see cref="PageScript"/>), which works everywhere except across an origin
    /// boundary: a captcha, an embedded sign-in or a payment widget is served from
    /// another host, so <c>elementFromPoint</c> returns the iframe element itself
    /// and a click dispatched on it does nothing. Issue #20 is that, on Instagram's
    /// reCAPTCHA, on the NUI package.
    ///
    /// The ewk build answers it with <c>evas_event_feed_mouse_*</c>. NUI has no Evas
    /// canvas to feed — the web view is a DALi actor drawn from a texture — and the
    /// managed surface at API 9 exposes no way to send one either: there is no
    /// <c>WebView.SendTouchEvent</c>, and <see cref="Touch"/> cannot be constructed
    /// with a position.
    ///
    /// One layer down it is all there. DALi's own C# binder,
    /// <c>libdali2-csharp-binder.so</c>, exports both halves —
    /// <c>CSharp_Dali_new_TouchPoint__SWIG_0</c> and
    /// <c>CSharp_Dali_Window_FeedTouch</c> — because TizenFX calls them itself from
    /// its internal <c>Interop</c> layer; only the managed wrappers are marked
    /// internal. So the entry points are P/Invoked here directly, exactly as
    /// <see cref="NativeEngine"/> and <see cref="NativeMouse"/> P/Invoke the EFL
    /// sonames.
    ///
    /// What the platform then does with it is the whole point:
    /// <c>DevelWindow::FeedTouchPoint</c> injects the point into DALi's core, which
    /// hit-tests it by screen position like any other touch, delivers it to the web
    /// view actor, and the toolkit's <c>WebView::OnTouchEvent</c> hands it to the
    /// engine. Chromium then routes it into whichever frame is under the point, and
    /// because it arrived as real input the event is trusted — which is the part a
    /// captcha checks.
    ///
    /// Why this is not simply used for every click: a real tap on a text field
    /// focuses it, and a focused field raises the platform IME, which swallows the
    /// remote — the exact failure the in-page pointer exists to avoid. So this stays
    /// a fallback for frames, not the default.
    ///
    /// Everything is best-effort. If the binder's name or a symbol differs on some
    /// firmware, the reason is recorded and the click behaves as it did before.
    /// </summary>
    internal static class NuiNativeTouch
    {
        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        private const string Binder = "libdali2-csharp-binder.so";

        /// <summary>Dali::PointState — Down is Started, Up is Finished.</summary>
        private const int StateDown = 0;
        private const int StateUp = 1;

        /// <summary>
        /// Any id that is not a real device works; DALi only uses it to keep the
        /// points of a multi-touch sequence together, and this feeds one at a time.
        /// </summary>
        private const int DeviceId = 0;

        /// <summary>
        /// Between press and release, in the timestamps handed to the engine. The
        /// two feeds happen in the same instant of wall-clock time, and a touch that
        /// starts and ends on the same millisecond is the kind of thing a site's
        /// own handling can discard.
        /// </summary>
        private const int TapMilliseconds = 60;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        /// <summary>Dali::TouchPoint(int deviceId, State state, float screenX, float screenY).</summary>
        [DllImport(Binder, EntryPoint = "CSharp_Dali_new_TouchPoint__SWIG_0")]
        private static extern IntPtr NewTouchPoint(int deviceId, int state, float x, float y);

        [DllImport(Binder, EntryPoint = "CSharp_Dali_delete_TouchPoint")]
        private static extern void DeleteTouchPoint(IntPtr point);

        /// <summary>Dali::DevelWindow::FeedTouchPoint(Window, const TouchPoint&amp;, int timeStamp).</summary>
        [DllImport(Binder, EntryPoint = "CSharp_Dali_Window_FeedTouch")]
        private static extern void FeedTouchPoint(IntPtr window, IntPtr point, int timeStamp);

        private static bool _probed;
        private static IntPtr _window;
        private static uint _stamp;

        /// <summary>Why the last attempt did nothing, for the diagnostics screen.</summary>
        public static string LastResult { get; private set; } = "(not used yet)";

        /// <summary>
        /// Taps at a point in window coordinates. Returns false when the platform
        /// would not let us, in which case nothing happened at all.
        /// </summary>
        public static bool Click(Window window, int x, int y)
        {
            IntPtr handle = HandleOf(window);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr down = IntPtr.Zero;
            IntPtr up = IntPtr.Zero;
            try
            {
                int at = NextStamp();

                // Two separate points rather than one reused: the binder owns the
                // native object and the state is fixed at construction.
                down = NewTouchPoint(DeviceId, StateDown, x, y);
                up = NewTouchPoint(DeviceId, StateUp, x, y);
                if (down == IntPtr.Zero || up == IntPtr.Zero)
                {
                    LastResult = "could not build a touch point";
                    DiagLog.Add("native touch: " + LastResult);
                    return false;
                }

                FeedTouchPoint(handle, down, at);
                FeedTouchPoint(handle, up, at + TapMilliseconds);

                LastResult = "fed tap at " + x + "," + y;
                DiagLog.Add("native touch: " + LastResult);
                return true;
            }
            catch (Exception ex)
            {
                _window = IntPtr.Zero;
                LastResult = "feed failed — " + ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("native touch: " + LastResult);
                return false;
            }
            finally
            {
                Release(down);
                Release(up);
            }
        }

        private static void Release(IntPtr point)
        {
            if (point == IntPtr.Zero)
            {
                return;
            }

            try
            {
                DeleteTouchPoint(point);
            }
            catch (Exception)
            {
                // A leaked TouchPoint is a few bytes, and this runs once per click
                // on a frame; failing the click over it would help nobody.
            }
        }

        /// <summary>
        /// The window's native pointer.
        ///
        /// TizenFX keeps it on <c>BaseHandle.SwigCPtr</c>, which is internal at API
        /// 9 — <c>GetBaseHandleCPtrHandleRef</c>, its public-facing twin, is internal
        /// here too. Reflection rather than a second guess at the layout: the
        /// property is the same object TizenFX itself passes to this very binder, so
        /// reading it is exactly as correct as the call it feeds, and a rename shows
        /// up as a named failure on the diagnostics screen instead of a wrong
        /// pointer.
        /// </summary>
        private static IntPtr HandleOf(Window window)
        {
            if (_window != IntPtr.Zero)
            {
                return _window;
            }

            if (window == null)
            {
                LastResult = "no window";
                return IntPtr.Zero;
            }

            if (_probed)
            {
                return IntPtr.Zero;
            }

            _probed = true;

            try
            {
                // Same reason as NativeEngine.Preload: the .NET loader on these sets
                // has been seen to mangle a soname past recognition, and an absolute
                // dlopen with RTLD_GLOBAL makes the later P/Invoke bind to the
                // already-loaded library. NUI has it open already; this only insists.
                dlopen("/usr/lib/" + Binder, RtldNow | RtldGlobal);
            }
            catch (Exception)
            {
                // Not fatal — resolution by name may well work on its own.
            }

            try
            {
                object value = Read(window, "SwigCPtr") ?? Read(window, "GetBaseHandleCPtrHandleRef");
                if (value == null)
                {
                    LastResult = "window handle not reachable (no SwigCPtr on " +
                                 window.GetType().Name + ")";
                    DiagLog.Add("native touch: " + LastResult);
                    return IntPtr.Zero;
                }

                _window = ((HandleRef)value).Handle;
                if (_window == IntPtr.Zero)
                {
                    LastResult = "window handle is null";
                    DiagLog.Add("native touch: " + LastResult);
                }

                return _window;
            }
            catch (Exception ex)
            {
                LastResult = "window handle unreadable — " + ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("native touch: " + LastResult);
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Reads a property by name from anywhere in the type's chain. Declared-only
        /// at each level because a non-public property is not inherited into the
        /// search of a derived type.
        /// </summary>
        private static object Read(object instance, string name)
        {
            for (Type type = instance.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperty(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                if (property != null && property.CanRead &&
                    property.PropertyType == typeof(HandleRef))
                {
                    return property.GetValue(instance);
                }
            }

            return null;
        }

        /// <summary>
        /// Timestamps have to keep increasing: the engine reads them to tell a tap
        /// from a long press, and a sequence that goes backwards is discarded.
        /// </summary>
        private static int NextStamp()
        {
            uint now = unchecked((uint)Environment.TickCount);
            _stamp = now > _stamp + TapMilliseconds ? now : _stamp + TapMilliseconds + 1;
            return unchecked((int)(_stamp & 0x7FFFFFFF));
        }
    }
}
