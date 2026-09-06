using System;
using System.Globalization;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// D-pad pointer for the NUI build. The position is a fraction of the
    /// viewport; the injected page script (<see cref="PageScript"/>) does the
    /// hit-testing and event dispatch, and this class translates key presses into
    /// script calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #78: "if we load a heavy site cursor start to become less responsive,
    /// when we load light site it is faster, default browser cursor perform same
    /// on all sites." That is exactly right, and it was ours. Every D-pad step used
    /// to be one <c>EvaluateJavaScript</c>, and the script it ran did an
    /// <c>elementFromPoint</c> and a <c>mousemove</c> dispatch — so the pointer
    /// could only move as fast as the page's main thread was willing to run our
    /// script, and on Instagram or Spotify that thread is the busiest thing on the
    /// television. The TV's own browser draws its pointer in the compositor, which
    /// is why his comparison holds.
    /// </para>
    /// <para>
    /// Two changes, and both are needed. **The pointer is drawn by us**, as DALi
    /// views over the web view, so where it appears owes the page nothing. And
    /// **the page is told where it is at most once a tick** rather than once a key
    /// press, because the hover the page does on being told is the expensive half
    /// and doing it twenty times during one held key press helps nobody. What the
    /// page loses by this is up to 150 ms of hover lag behind the drawn pointer;
    /// what it must never lose is agreement at the moment of a click, which is why
    /// <see cref="Click"/> flushes a pending move in the same evaluation.
    /// </para>
    /// <para>
    /// The old header said an overlay would have to track scroll and zoom itself.
    /// It would not: the position is a fraction of the *viewport*, and the viewport
    /// is the view's own rectangle, which is the whole reason it is kept as a
    /// fraction. The drawn shape is a ringed dot rather than the page script's
    /// arrow, because an Evas or DALi view has no triangle and the arrow is drawn
    /// out of CSS borders. A dot that keeps up beats an arrow that does not.
    /// </para>
    /// </remarks>
    internal sealed class NuiCursor
    {
        // A fraction of the viewport, so on a 1920x1080 set the smallest step is
        // about 13px across and 8px down. It used to be 0.020 — 38px across — and
        // issue #20's reporter could not land on Instagram's mute button or the ✕
        // that closes a reel, because a step wider than the target can straddle it
        // whatever it starts from. The floor is what has to be smaller than the
        // smallest thing worth clicking; the ceiling is what crosses the screen, and
        // holding the key still gets there in about eight repeats.
        private const double StepMin = 0.007;
        private const double StepMax = 0.110;
        private const double StepGrowth = 1.50;
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromMilliseconds(220);

        /// <summary>
        /// The drawn pointer, outside-in: a dark ring so it survives a white page,
        /// a light body, an accent centre small enough to aim with. The same three
        /// shapes and sizes the ElmSharp build has drawn since it gained a native
        /// pointer, so the two builds look like one product.
        /// </summary>
        private const int RingSize = 30;
        private const int BodySize = 22;
        private const int CoreSize = 8;

        private readonly WebView _web;
        private readonly Window _window;
        private readonly View _ring;
        private readonly View _body;
        private readonly View _core;

        private double _x = 0.5;
        private double _y = 0.5;
        private double _step = StepMin;
        private DateTime _lastMove = DateTime.MinValue;
        private int _lastDx;
        private int _lastDy;

        /// <summary>
        /// A move the page has not been told about yet. Cleared by
        /// <see cref="FlushPending"/> on the app's tick, or by a click, whichever
        /// comes first.
        /// </summary>
        private bool _movePending;

        /// <summary>Whether the pointer is drawn at all — see <see cref="Hide"/>.</summary>
        private bool _visible = true;

        /// <summary>
        /// Whether the views were built at all. False leaves the page's own arrow
        /// in place and every decision below with only one answer.
        /// </summary>
        private readonly bool _built;

        private CursorVisual _visual = CursorVisual.Native;

        public NuiCursor(Window window, WebView web)
        {
            _web = web;
            _window = window;

            try
            {
                _ring = Blob(RingSize, new Color(0f, 0f, 0f, 0.88f));
                _body = Blob(BodySize, NuiTheme.Ink);
                _core = Blob(CoreSize, NuiTheme.Accent);
                _built = true;
            }
            catch (Exception ex)
            {
                // Best-effort like everything else past the managed surface. A
                // pointer we cannot draw is not a browser that cannot run: the page
                // script keeps its own arrow and the app behaves as it did before.
                DiagLog.Add("native pointer unavailable, using the page's own: " + ex.Message);
                _built = false;
            }
        }

        /// <summary>Who is drawing the pointer at the moment.</summary>
        public CursorVisual Visual
        {
            get { return _built && _visual == CursorVisual.Native ? CursorVisual.Native : CursorVisual.Dom; }
        }

        /// <summary>
        /// Swaps the page's arrow for ours and back. Native is the default and the
        /// answer to issue #78; the way back exists because the arrow is the better
        /// looking of the two and on a light page somebody may prefer it.
        /// </summary>
        public void SetVisual(CursorVisual visual)
        {
            _visual = visual;
            Apply();
            DiagLog.Add("pointer drawn by " + (Visual == CursorVisual.Native ? "the app" : "the page"));
        }

        /// <summary>
        /// Puts the current choice on the screen and on the page: exactly one of the
        /// two pointers is ever showing, and the position is re-sent either way.
        /// </summary>
        private void Apply()
        {
            if (Visual == CursorVisual.Native)
            {
                Eval("try{window." + PageScript.Namespace + ".hide();}catch(e){}");
                Place();
            }
            else
            {
                ShowViews(false);

                // The page's arrow is put back by install(), which is idempotent
                // and is the only call that sets it visible again.
                Eval(PageScript.Install("sbnative"));
            }

            MoveInPage();
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

            // A load put the page's own arrow back. Two pointers on one screen is
            // worse than either, and the page's is the one that lags.
            // Not deferred: the page has just been replaced and knows nothing about
            // where the pointer is, so the first telling is worth a call of its own.
            _movePending = false;
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

            // Drawn now, told later. This is the whole of issue #78: the drawing is
            // ours and costs the page nothing, and the page hears about it on the
            // next tick instead of on every repeat of a held key.
            Mark();
        }

        public void Center()
        {
            _x = 0.5;
            _y = 0.5;
            Mark();
        }

        /// <summary>
        /// Draws the pointer where it now is and notes that the page has not been
        /// told. With the page drawing it there is nothing to draw and nothing to
        /// wait for, so the telling happens at once — the delay only buys anything
        /// when something else is already showing the user where they are.
        /// </summary>
        private void Mark()
        {
            if (Visual == CursorVisual.Native)
            {
                Place();
                _movePending = true;
            }
            else
            {
                _movePending = false;
                MoveInPage();
            }
        }

        /// <summary>
        /// Tells the page where the pointer is, if it has not been told since the
        /// last move. Called from the app's 150 ms tick, which is what turns a burst
        /// of key repeats into one hover update.
        /// </summary>
        public void FlushPending()
        {
            if (!_movePending)
            {
                return;
            }

            _movePending = false;
            MoveInPage();
        }

        public void Click()
        {
            try
            {
                // A pending move rides along in the same evaluation rather than
                // being sent as a call before it. The script's click() hit-tests at
                // the position move() left behind, so a click that overtook its own
                // move would land wherever the pointer used to be — and two
                // evaluations would be two chances for that ordering to go wrong.
                string move = string.Empty;
                if (_movePending)
                {
                    _movePending = false;
                    move = "window." + PageScript.Namespace + ".move(" + F(_x) + "," + F(_y) + ");";
                }

                _web.EvaluateJavaScript(
                    "(function(){if(!window." + PageScript.Namespace + "){return 'no page script';}" +
                    move + "return String(window." + PageScript.Namespace + ".click());})()",
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

        /// <summary>
        /// Forgets any real input the page has seen so far, so the next reading is
        /// about the next feed and nothing else.
        /// </summary>
        public void ClearNativeWitness()
        {
            Eval("try{window." + PageScript.Namespace + ".clearNative();}catch(e){}");
        }

        /// <summary>
        /// Asks the page, after <paramref name="delayMilliseconds"/>, whether real
        /// input actually arrived — see <c>PageScript.native()</c>.
        ///
        /// The delay is the point. Feeding a touch into the window and reading the
        /// answer on the next line would always report "nothing": the point is queued
        /// for DALi's next update, handed to the engine after that, and delivered to
        /// the page after that again. Anything sooner measures our own impatience.
        /// </summary>
        public void ReportNativeWitness(int delayMilliseconds, Action<string> report)
        {
            NuiLater.Once(delayMilliseconds, delegate
            {
                try
                {
                    _web.EvaluateJavaScript(
                        "String(window." + PageScript.Namespace + " && window." +
                        PageScript.Namespace + ".native())",
                        result =>
                        {
                            if (report != null)
                            {
                                report(string.IsNullOrEmpty(result) ? "(no answer)" : result);
                            }
                        });
                }
                catch (Exception ex)
                {
                    DiagLog.Add("witness read failed: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Takes the pointer off the screen entirely — both drawings of it. Used
        /// when the remote's keys are handed to the page (key 4), where there is no
        /// pointer to aim.
        /// </summary>
        public void Hide()
        {
            _visible = false;
            ShowViews(false);
            Eval("try{window." + PageScript.Namespace + ".hide();}catch(e){}");
        }

        /// <summary>Brings it back after <see cref="Hide"/>.</summary>
        public void Show()
        {
            _visible = true;
            Apply();
        }

        /// <summary>
        /// One circle of the drawn pointer. Sensitive is cleared for the same
        /// reason the chrome clears it: DALi delivers a fed touch to the front-most
        /// sensitive actor, and the frame-click path feeds touches at exactly the
        /// point this sits on — a pointer that could swallow its own click would be
        /// a very hard evening to explain.
        /// </summary>
        private View Blob(int size, Color colour)
        {
            var view = new View
            {
                Size2D = new Size2D(size, size),
                BackgroundColor = colour,
                CornerRadius = size / 2f,
                Sensitive = false,
            };
            _window.Add(view);
            view.Hide();
            return view;
        }

        private void Place()
        {
            if (!_built || !_visible)
            {
                return;
            }

            try
            {
                Size2D screen = _window.WindowSize;
                int cx = (int)Math.Round(_x * screen.Width);
                int cy = (int)Math.Round(_y * screen.Height);

                Centre(_ring, cx, cy, RingSize);
                Centre(_body, cx, cy, BodySize);
                Centre(_core, cx, cy, CoreSize);
                ShowViews(true);
            }
            catch (Exception ex)
            {
                DiagLog.Add("could not place the pointer: " + ex.Message);
            }
        }

        private static void Centre(View view, int cx, int cy, int size)
        {
            if (view != null)
            {
                view.Position2D = new Position2D(cx - (size / 2), cy - (size / 2));
            }
        }

        private void ShowViews(bool visible)
        {
            if (!_built)
            {
                return;
            }

            try
            {
                foreach (View view in new[] { _ring, _body, _core })
                {
                    if (view == null)
                    {
                        continue;
                    }

                    if (visible)
                    {
                        view.Show();
                        view.RaiseToTop();
                    }
                    else
                    {
                        view.Hide();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("could not show the pointer: " + ex.Message);
            }
        }

        private void Detach(View view)
        {
            try
            {
                if (view != null)
                {
                    _window.Remove(view);
                    view.Dispose();
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("could not remove the pointer: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes the drawn pointer off the screen and out of the window. Called when
        /// the view it sits over is being replaced, so a rebuilt view does not end
        /// up with a second pointer over it.
        /// </summary>
        public void Remove()
        {
            Detach(_ring);
            Detach(_body);
            Detach(_core);
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
