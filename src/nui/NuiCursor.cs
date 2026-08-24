using System;
using System.Globalization;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// D-pad pointer for the NUI build. Same idea as <see cref="VirtualCursor"/>:
    /// the position is a fraction of the viewport and the injected page script
    /// (<see cref="PageScript"/>) does the hit-testing and event dispatch, so this
    /// class only has to translate key presses into script calls.
    ///
    /// The cursor is drawn by the page here — no Evas-overlay variant — because
    /// NUI composites the web view as a texture and an overlay View would need to
    /// track scroll and zoom itself.
    /// </summary>
    internal sealed class NuiCursor
    {
        private const double StepMin = 0.020;
        private const double StepMax = 0.110;
        private const double StepGrowth = 1.35;
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(220);

        private readonly WebView _web;

        private double _x = 0.5;
        private double _y = 0.5;
        private double _step = StepMin;
        private DateTime _lastMove = DateTime.MinValue;
        private int _lastDx;
        private int _lastDy;

        public NuiCursor(WebView web)
        {
            _web = web;
        }

        /// <summary>
        /// Raised with what the click hit. The app uses the script's FIELD: prefix to
        /// open the on-screen keyboard for a text field, since fields are never
        /// focused (focusing one raises the platform IME).
        /// </summary>
        public event Action<string> Clicked;

        /// <summary>
        /// Where the pointer is, as a fraction of the viewport. Needed by the
        /// native touch path, which has to turn it back into window pixels.
        /// </summary>
        public double FractionX
        {
            get { return _x; }
        }

        /// <summary>See <see cref="FractionX"/>.</summary>
        public double FractionY
        {
            get { return _y; }
        }

        public void Reinstall()
        {
            // The ElmSharp build needs a bridge name for click feedback; NUI gets
            // results back through EvaluateJavaScript callbacks, so the name is
            // only used for the (unused) postMessage path.
            Eval(PageScript.Install("sbnative"));
            MoveInPage();
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
            MoveInPage();
        }

        public void Center()
        {
            _x = 0.5;
            _y = 0.5;
            MoveInPage();
        }

        public void Click()
        {
            try
            {
                _web.EvaluateJavaScript(
                    "String(window." + PageScript.Namespace + " && window." + PageScript.Namespace + ".click())",
                    result =>
                    {
                        DiagLog.Add("click -> " + (result ?? "(null)"));
                        Action<string> handler = Clicked;
                        if (handler != null)
                        {
                            handler(result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("click failed: " + ex.Message);
            }
        }

        public void ScrollPage(int direction)
        {
            // Reported back, unlike the fire-and-forget Eval: without this there is
            // no way to tell a scroll that did nothing from a key that never
            // arrived. 'el' = scrolled a container, 'doc' = scrolled the document.
            try
            {
                _web.EvaluateJavaScript(
                    "String(window." + PageScript.Namespace + ".page(" +
                    direction.ToString(CultureInfo.InvariantCulture) + "))",
                    result =>
                    {
                        if (!string.IsNullOrEmpty(result))
                        {
                            DiagLog.Add("scroll " + (direction < 0 ? "up" : "down") + " -> " + result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("scroll failed: " + ex.Message);
            }
        }

        private void MoveInPage()
        {
            Eval("try{window." + PageScript.Namespace + ".move(" + F(_x) + "," + F(_y) + ");}catch(e){}");
        }

        private void Eval(string script)
        {
            try
            {
                _web.EvaluateJavaScript(script);
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
