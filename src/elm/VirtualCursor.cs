using System;
using System.Globalization;
using ElmSharp;
using Tizen.WebView;

namespace Overscan
{
    internal enum CursorVisual
    {
        /// <summary>Cursor drawn by the injected page script (survives page scroll).</summary>
        Dom,

        /// <summary>Cursor drawn as an Evas overlay above the web view.</summary>
        Native,
    }

    /// <summary>
    /// D-pad driven pointer. Position is kept as a fraction of the viewport so the
    /// native side never has to know the page's CSS pixel size, zoom or DPR — the
    /// injected script converts on arrival.
    /// </summary>
    internal sealed class VirtualCursor
    {
        private const double StepMin = 0.020;
        private const double StepMax = 0.110;
        private const double StepGrowth = 1.35;

        /// <summary>Repeats closer together than this accelerate the cursor.</summary>
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(220);

        private readonly WebView _web;
        private readonly string _bridge;
        private readonly Rectangle _ring;
        private readonly Rectangle _dot;
        private readonly Rectangle _core;

        private double _x = 0.5;
        private double _y = 0.5;
        private double _step = StepMin;
        private DateTime _lastMove = DateTime.MinValue;
        private int _lastDx;
        private int _lastDy;

        public VirtualCursor(Window window, WebView web, string bridgeName)
        {
            _web = web;
            _bridge = bridgeName;

            // Three stacked rectangles make a target that stays legible on any page:
            // dark ring, light body, accent centre. Rounded shapes are not available
            // to an Evas rectangle.
            _ring = new Rectangle(window) { Color = Color.FromRgba(0, 0, 0, 225) };
            _dot = new Rectangle(window) { Color = Theme.Ink };
            _core = new Rectangle(window) { Color = Theme.Accent };
        }

        public CursorVisual Visual { get; private set; } = CursorVisual.Dom;

        public void ToggleVisual()
        {
            Visual = Visual == CursorVisual.Dom ? CursorVisual.Native : CursorVisual.Dom;
            DiagLog.Add("cursor visual = " + Visual);
            Apply();
        }

        /// <summary>Re-asserts the cursor after a page load replaced the DOM.</summary>
        public void Reinstall()
        {
            Eval(PageScript.Install(_bridge));
            Apply();
        }

        public void Move(int dx, int dy)
        {
            DateTime now = DateTime.UtcNow;
            bool sameDirection = dx == _lastDx && dy == _lastDy;
            _step = sameDirection && now - _lastMove < RepeatWindow
                ? Math.Min(StepMax, _step * StepGrowth)
                : StepMin;
            _lastMove = now;
            _lastDx = dx;
            _lastDy = dy;

            _x = Clamp(_x + (dx * _step));
            _y = Clamp(_y + (dy * _step));
            Apply();
        }

        public void Center()
        {
            _x = 0.5;
            _y = 0.5;
            Apply();
        }

        public void Click()
        {
            // The click result comes back over the message bridge instead of a
            // return value: ewk's script-execute is fire-and-forget on API 5.
            Eval("try{window." + _bridge + ".postMessage('click\\u0001'+window." +
                 PageScript.Namespace + ".click());}catch(e){}");
        }

        public void ScrollPage(int direction)
        {
            Eval("try{window." + PageScript.Namespace + ".page(" +
                 direction.ToString(CultureInfo.InvariantCulture) + ");}catch(e){}");
        }

        public void Hide()
        {
            _ring.Hide();
            _dot.Hide();
            _core.Hide();
            Eval("try{window." + PageScript.Namespace + ".hide();}catch(e){}");
        }

        private void Apply()
        {
            if (Visual == CursorVisual.Native)
            {
                Eval("try{window." + PageScript.Namespace + ".hide();}catch(e){}");
                PlaceNative();
                // Still report the position so hover states and the click target
                // stay in sync with what is drawn.
                MoveInPage();
            }
            else
            {
                _ring.Hide();
                _dot.Hide();
                _core.Hide();
                MoveInPage();
            }
        }

        private void MoveInPage()
        {
            Eval("try{window." + PageScript.Namespace + ".move(" + F(_x) + "," + F(_y) + ");}catch(e){}");
        }

        private void PlaceNative()
        {
            Rect area = _web.Geometry;
            int cx = area.X + (int)Math.Round(_x * area.Width);
            int cy = area.Y + (int)Math.Round(_y * area.Height);

            _ring.Geometry = new Rect(cx - 15, cy - 15, 30, 30);
            _dot.Geometry = new Rect(cx - 11, cy - 11, 22, 22);
            _core.Geometry = new Rect(cx - 4, cy - 4, 8, 8);
            _ring.Show();
            _dot.Show();
            _core.Show();
            _ring.RaiseTop();
            _dot.RaiseTop();
            _core.RaiseTop();
        }

        private void Eval(string script)
        {
            try
            {
                _web.Eval(script);
            }
            catch (Exception ex)
            {
                DiagLog.Add("eval failed: " + ex.Message);
            }
        }

        private static string F(double value)
        {
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static double Clamp(double value)
        {
            return value < 0 ? 0 : (value > 1 ? 1 : value);
        }
    }
}
