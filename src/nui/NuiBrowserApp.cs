using System;
using System.Globalization;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// The NUI build, for platforms where <c>Tizen.WebView</c> is gone.
    ///
    /// Tizen 10.0 ships no Tizen.WebView.dll at all — the ElmSharp build dies there
    /// with a FileNotFoundException the moment it touches the type. NUI's WebView is
    /// public API from API 9, so this build covers 9.0+ while the ElmSharp build
    /// covers 5.0-8.0. Everything engine-independent (user agents, the injected
    /// page script, storage, diagnostics) is shared between them.
    /// </summary>
    internal sealed class NuiBrowserApp : NUIApplication
    {
        private Window _window;
        private WebView _web;
        private NuiCursor _cursor;
        private NuiKeyboard _keyboard;

        private View _bar;
        private TextLabel _host;
        private TextLabel _path;
        private TextLabel _status;
        private View _progress;
        private View _hints;
        private TextLabel _overlay;

        private UserAgentPreset[] _presets = UserAgents.Defaults();
        private int _presetIndex;
        private string _engineUserAgentRaw;
        private string _engineUserAgent = "(not read yet)";
        private string _lastProbe = "(no page probed yet)";
        private string _lastMetrics = "(not measured yet)";
        private string _engineFailure;

        /// <summary>
        /// Cached on the main thread: DiagServer answers on its own thread and DALi
        /// objects are not thread-safe, so the report may only read plain strings.
        /// </summary>
        private volatile string _cachedUrl = "-";
        private volatile string _cachedTitle = "(untitled)";
        private volatile string _cachedGeometry = "(unmeasured)";

        private bool _overlayVisible;
        private bool _hintsWanted = true;
        private bool _imagesOn = true;
        private bool _keysToPage;
        private bool _viewportFix;
        private bool _atHome;
        private bool _loading;
        private bool _chromeVisible;
        private int _marquee;
        private DateTime _lastActivity = DateTime.UtcNow;
        private DateTime _flashUntil = DateTime.MinValue;

        /// <summary>The page to open at launch, or null for the start screen.</summary>
        private string _startupUrl;

        protected override void OnCreate()
        {
            base.OnCreate();
            DiagServer.ReportProvider = Report;

            _window = GetDefaultWindow();
            _window.BackgroundColor = new Color(0.039f, 0.043f, 0.055f, 1f);
            DiagLog.Add("window " + _window.WindowSize.Width + "x" + _window.WindowSize.Height);

            // Before the keyboard is built: it resolves its remembered layout the
            // first time KeyboardLayouts is touched, so initialising the store
            // afterwards silently threw the user's layout choice away.
            Store.Init(DirectoryInfo.Data);

            BuildChrome();
            BuildOverlay();
            BuildHints();

            _keyboard = new NuiKeyboard(_window);
            _keyboard.Committed += OnKeyboardCommitted;
            _keyboard.StartPageSet += OnStartPageSet;
            _window.KeyEvent += OnWindowKey;

            if (!TryStartEngine())
            {
                _status.Text = "web engine failed — press 3";
                ShowOverlay(true);
                return;
            }

            _engineUserAgentRaw = SafeUserAgent();
            _engineUserAgent = _engineUserAgentRaw ?? "(unavailable)";
            _presets[0] = UserAgents.MatchingEngine(_engineUserAgentRaw);
            DiagLog.Add("engine UA: " + _engineUserAgent);

            _viewportFix = Store.GetBool("viewportFix", false);
            _hintsWanted = Store.GetBool("hints", true);
            _startupUrl = Store.Get("startupUrl", null);
            ShowHints(_hintsWanted);

            ApplyPreset(Math.Min(Store.GetInt("uaPreset", 0), _presets.Length - 1));

            // A start page, if one was set with the keyboard's `start` key (issue
            // #15): otherwise the same address gets typed on a remote every launch.
            if (string.IsNullOrEmpty(_startupUrl))
            {
                ShowHome();
            }
            else
            {
                Navigate(_startupUrl);
            }

            var timer = new Timer(150);
            timer.Tick += (s, e) => OnTick();
            timer.Start();
        }

        private bool TryStartEngine()
        {
            try
            {
                Size2D screen = _window.WindowSize;

                // Full-window: the chrome floats above the page rather than taking
                // layout space, so nothing is letterboxed.
                _web = new WebView
                {
                    Position = new Position(0, 0),
                    Size = new Size(screen.Width, screen.Height),
                };
                _window.Add(_web);
                DiagLog.Add("NUI WebView created");

                // The chrome is drawn over the page but must never be *hit* by it:
                // DALi delivers a fed touch to the front-most sensitive actor, and
                // the hints card alone covers a corner big enough to hide a captcha.
                // Nothing here is ever touched deliberately — the app is driven
                // entirely by the remote — so none of it needs to be sensitive.
                PassTouchesThrough(_bar);
                PassTouchesThrough(_progress);
                PassTouchesThrough(_overlay);
                PassTouchesThrough(_hints);

                // Insurance for the frame-click path: the toolkit gates part of its
                // pointer forwarding on this, and the default is not worth guessing.
                _web.MouseEventsEnabled = true;

                _web.EnableJavaScript = true;
                if (_web.Settings != null)
                {
                    _web.Settings.JavaScriptEnabled = true;

                    // The platform IME is what makes a page's autofocus swallow the
                    // remote. NUI exposes the switches for it that API 5 does not.
                    _web.Settings.ImePanelEnabled = false;
                    _web.Settings.KeypadWithoutUserActionUsed = false;

                    // Auto-fitting is the mobile-style "lay out wide, then scale to
                    // fit" behaviour, i.e. the suspected cause of the stretched
                    // rendering on older sets. We want 1:1.
                    _web.Settings.AutoFittingEnabled = false;
                    _imagesOn = Store.GetBool("images", true);
                    _web.Settings.AutomaticImageLoadingAllowed = _imagesOn;
                }

                _web.PageLoadStarted += (s, e) =>
                {
                    DiagLog.Add("load started");
                    _loading = true;
                    ShowChrome();
                };

                _web.PageLoadFinished += (s, e) =>
                {
                    _loading = false;
                    _progress.Hide();
                    DiagLog.Add("load finished: " + SafeUrl());
                    _cursor.Reinstall();
                    Probe();
                    ApplyViewportFix();
                    Store.RecordVisit(SafeUrl(), SafeTitle());
                    UpdateStatus();
                };

                _web.PageLoadError += (s, e) =>
                {
                    _loading = false;
                    _progress.Hide();
                    DiagLog.Add("load error on " + SafeUrl());
                    UpdateStatus();
                };

                _cursor = new NuiCursor(_web);
                _cursor.Clicked += OnPageClicked;
                DiagLog.Add("engine ready");
                return true;
            }
            catch (Exception ex)
            {
                _engineFailure = ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("ENGINE FAILURE " + _engineFailure);
                return false;
            }
        }

        /// <summary>
        /// Takes a chrome element out of hit-testing without hiding it. Best-effort:
        /// on a build where the property is missing the frame click simply has the
        /// odds it had before.
        /// </summary>
        private static void PassTouchesThrough(View view)
        {
            try
            {
                if (view != null)
                {
                    view.Sensitive = false;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("could not clear Sensitive: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- chrome

        private void BuildChrome()
        {
            Size2D screen = _window.WindowSize;

            _bar = new View
            {
                Position2D = new Position2D(0, 0),
                Size2D = new Size2D(screen.Width, NuiTheme.BarHeight),
                BackgroundColor = NuiTheme.Panel,
            };
            _window.Add(_bar);

            // Two labels rather than markup: the host has to read from the sofa, the
            // rest of the URL only has to be present.
            _host = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, 14),
                Size2D = new Size2D(screen.Width / 3, NuiTheme.BarHeight - 24),
                PointSize = 15,
                TextColor = NuiTheme.Ink,
                Text = "Overscan",
            };
            _bar.Add(_host);

            _path = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad + (screen.Width / 3) + 12, 20),
                Size2D = new Size2D(screen.Width / 4, NuiTheme.BarHeight - 28),
                PointSize = 11,
                TextColor = NuiTheme.InkMuted,
                Text = string.Empty,
            };
            _bar.Add(_path);

            _status = new TextLabel
            {
                Position2D = new Position2D(screen.Width - (screen.Width / 3) - NuiTheme.Pad, 20),
                Size2D = new Size2D(screen.Width / 3, NuiTheme.BarHeight - 28),
                PointSize = 11,
                TextColor = NuiTheme.InkMuted,
                HorizontalAlignment = HorizontalAlignment.End,
                Text = "starting…",
            };
            _bar.Add(_status);

            _progress = new View
            {
                Position2D = new Position2D(0, NuiTheme.BarHeight - 4),
                Size2D = new Size2D(0, 4),
                BackgroundColor = NuiTheme.Accent,
            };
            _window.Add(_progress);

            ShowChrome();
        }

        private void ShowChrome()
        {
            _lastActivity = DateTime.UtcNow;
            if (_chromeVisible)
            {
                return;
            }

            _chromeVisible = true;
            _bar.Show();
            _bar.RaiseToTop();
        }

        private void HideChrome()
        {
            _chromeVisible = false;
            _bar.Hide();
            _progress.Hide();
        }

        /// <summary>
        /// Animates the loading marquee and hides the chrome once idle. An
        /// indeterminate bar rather than a real one, to match the ElmSharp build
        /// where LoadProgress is not available.
        /// </summary>
        private bool OnTick()
        {
            if (_loading && _chromeVisible)
            {
                int width = _window.WindowSize.Width;
                int span = width / 5;
                _marquee = (_marquee + (span / 4)) % (width + span);
                int x = Math.Max(0, _marquee - span);
                _progress.Position2D = new Position2D(x, NuiTheme.BarHeight - 4);
                _progress.Size2D = new Size2D(Math.Min(span, width - x), 4);
                _progress.Show();
                _progress.RaiseToTop();
            }

            if (_flashUntil != DateTime.MinValue && DateTime.UtcNow > _flashUntil)
            {
                _flashUntil = DateTime.MinValue;
                UpdateStatus();
            }

            bool busy = _loading || _keyboard.IsVisible || _overlayVisible;
            if (_chromeVisible && !busy && DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(4))
            {
                HideChrome();
            }

            return true;
        }

        private void BuildOverlay()
        {
            Size2D screen = _window.WindowSize;
            _overlay = new TextLabel
            {
                Position2D = new Position2D(48, NuiTheme.BarHeight + 24),
                Size2D = new Size2D(screen.Width - 96, screen.Height - NuiTheme.BarHeight - 72),
                PointSize = 9,
                TextColor = NuiTheme.Ink,
                BackgroundColor = NuiTheme.PanelDeep,
                MultiLine = true,
                Text = string.Empty,
            };
            _overlay.Hide();
            _window.Add(_overlay);
        }

        private void BuildHints()
        {
            Size2D screen = _window.WindowSize;
            int width = 470;
            int height = 470;

            _hints = new View
            {
                Position2D = new Position2D(screen.Width - width - 40, screen.Height - height - 40),
                Size2D = new Size2D(width, height),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            var title = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, NuiTheme.Pad),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), 34),
                PointSize = 12,
                TextColor = NuiTheme.Accent,
                Text = "REMOTE",
            };
            _hints.Add(title);

            string[][] rows =
            {
                new[] { "0", "address bar" },
                new[] { "1", "identify as…" },
                new[] { "2", "type in field" },
                new[] { "3", "diagnostics" },
                new[] { "4", "keys to page" },
                new[] { "6", "fit page" },
                new[] { "7", "hide this" },
                new[] { "8", "keep page" },
                new[] { "9", "start screen" },
            };

            for (int i = 0; i < rows.Length; i++)
            {
                int y = NuiTheme.Pad + 52 + (i * 42);
                _hints.Add(new TextLabel
                {
                    Position2D = new Position2D(NuiTheme.Pad, y),
                    Size2D = new Size2D(60, 38),
                    PointSize = 13,
                    TextColor = NuiTheme.Ink,
                    Text = rows[i][0],
                });
                _hints.Add(new TextLabel
                {
                    Position2D = new Position2D(NuiTheme.Pad + 60, y + 3),
                    Size2D = new Size2D(width - NuiTheme.Pad - 80, 34),
                    PointSize = 11,
                    TextColor = NuiTheme.InkMuted,
                    Text = rows[i][1],
                });
            }

            _window.Add(_hints);
        }

        private void ShowHints(bool visible)
        {
            if (visible)
            {
                _hints.Show();
                _hints.RaiseToTop();
            }
            else
            {
                _hints.Hide();
            }
        }

        // ----------------------------------------------------------------- input

        private void OnWindowKey(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down)
            {
                return;
            }

            string key = e.Key.KeyPressedName;
            ShowChrome();

            if (_keyboard != null && _keyboard.IsVisible && _keyboard.HandleKey(key))
            {
                return;
            }

            if (_web == null)
            {
                if (key == RemoteKeys.Num3)
                {
                    ShowOverlay(!_overlayVisible);
                }
                else if (key == RemoteKeys.Back)
                {
                    Exit();
                }

                return;
            }

            switch (key)
            {
                case RemoteKeys.Left:
                    if (!_keysToPage) { _cursor.Move(-1, 0); }
                    break;
                case RemoteKeys.Right:
                    if (!_keysToPage) { _cursor.Move(1, 0); }
                    break;
                case RemoteKeys.Up:
                    if (!_keysToPage) { _cursor.Move(0, -1); }
                    break;
                case RemoteKeys.Down:
                    if (!_keysToPage) { _cursor.Move(0, 1); }
                    break;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    if (!_keysToPage) { _cursor.Click(); }
                    break;

                case RemoteKeys.ChannelUp:
                    _cursor.ScrollPage(-1);
                    break;
                case RemoteKeys.ChannelDown:
                    _cursor.ScrollPage(1);
                    break;

                case RemoteKeys.Back:
                    if (_overlayVisible)
                    {
                        ShowOverlay(false);
                    }
                    else if (_web.CanGoBack())
                    {
                        _web.GoBack();
                    }
                    else if (!_atHome)
                    {
                        ShowHome();
                    }
                    else
                    {
                        Exit();
                    }

                    break;

                case RemoteKeys.Num0:
                case RemoteKeys.More:
                case RemoteKeys.Menu:
                    _keyboard.Open(KeyboardTarget.Address, _atHome ? string.Empty : _cachedUrl);
                    break;

                case RemoteKeys.Num1:
                    ApplyPreset((_presetIndex + 1) % _presets.Length);
                    Store.Set("uaPreset", _presetIndex);
                    _web.Reload();
                    break;

                case RemoteKeys.Num2:
                    _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    break;

                case RemoteKeys.Num3:
                    ShowOverlay(!_overlayVisible);
                    break;

                case RemoteKeys.Num4:
                    _keysToPage = !_keysToPage;
                    DiagLog.Add(_keysToPage ? "keys -> page" : "keys -> cursor");
                    UpdateStatus();
                    break;

                case RemoteKeys.Num6:
                    _viewportFix = !_viewportFix;
                    Store.Set("viewportFix", _viewportFix);
                    DiagLog.Add("viewport fix " + (_viewportFix ? "ON" : "OFF"));
                    ApplyViewportFix();
                    UpdateStatus();
                    break;

                case RemoteKeys.Num7:
                    _hintsWanted = !_hintsWanted;
                    ShowHints(_hintsWanted);
                    Store.Set("hints", _hintsWanted);
                    break;

                case RemoteKeys.Num8:
                    ToggleFavourite();
                    break;

                case RemoteKeys.Num9:
                    ShowHome();
                    break;

                case RemoteKeys.Info:
                    ToggleImages();
                    break;

                default:
                    DiagLog.Add("unhandled key: " + key);
                    break;
            }
        }

        /// <summary>
        /// What the page script reported about the click. A FIELD: prefix means a
        /// text field: it cannot be focused (that raises the IME), so opening the
        /// grid is how text gets in. FRAME: means the click landed on a frame from
        /// another origin, which no script can reach into.
        /// </summary>
        private void OnPageClicked(string result)
        {
            if (result == null)
            {
                return;
            }

            if (result.StartsWith("FIELD:", StringComparison.Ordinal))
            {
                _keyboard.Open(KeyboardTarget.PageField, string.Empty);
            }
            else if (result.StartsWith("FRAME:", StringComparison.Ordinal))
            {
                // A cross-origin frame — a captcha, an embedded sign-in. Nothing in
                // the page script can reach inside it, so the click has to be a real
                // one. Issue #20 is this, on Instagram's reCAPTCHA.
                DiagLog.Add("click landed on a cross-origin frame: " + result);
                ClickThroughFrame();
            }
        }

        /// <summary>
        /// Taps where the pointer is, through DALi rather than through the page.
        ///
        /// The pointer's position is a fraction of the viewport, and the web view
        /// fills the window, so the window's own pixels are the conversion. See
        /// <see cref="NuiNativeTouch"/> for why this is a real touch and not a
        /// dispatched event.
        /// </summary>
        private void ClickThroughFrame()
        {
            try
            {
                Size2D screen = _window.WindowSize;
                int x = (int)Math.Round(_cursor.FractionX * screen.Width);
                int y = (int)Math.Round(_cursor.FractionY * screen.Height);

                if (!NuiNativeTouch.Click(_window, x, y))
                {
                    Flash("This frame cannot be clicked");
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("frame click failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Remembers what was typed as the page to open at launch (issue #15). An
        /// empty entry clears it and the start screen comes back.
        /// </summary>
        private void OnStartPageSet(string text)
        {
            if (string.IsNullOrEmpty((text ?? string.Empty).Trim()))
            {
                _startupUrl = null;
                Store.Set("startupUrl", string.Empty);
                DiagLog.Add("start page cleared");
                Flash("Start page cleared");
                return;
            }

            _startupUrl = Urls.Normalize(text);
            Store.Set("startupUrl", _startupUrl);
            DiagLog.Add("start page = " + _startupUrl);
            Flash("Opens here from now on");
            Navigate(_startupUrl);
        }

        private void OnKeyboardCommitted(string text, KeyboardTarget target)
        {
            if (target == KeyboardTarget.Address)
            {
                Navigate(Urls.Normalize(text));
                return;
            }

            // One evaluation, not two: NUI keeps a single pending result handler, so
            // overlapping calls deliver both replies to the last one registered.
            try
            {
                _web.EvaluateJavaScript(
                    "(function(){var t=String(window." + PageScript.Namespace + ".type(" +
                    Urls.JsString(text) + "));var s=String(window." + PageScript.Namespace +
                    ".submit());return 'typed into '+t+', submit -> '+s;})()",
                    result =>
                    {
                        if (!string.IsNullOrEmpty(result))
                        {
                            DiagLog.Add(result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("typing failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ navigation

        private void ShowHome()
        {
            try
            {
                _atHome = true;
                _web.LoadHtmlString(HomePage.Build(Store.AllFavourites, Store.RecentHistory, Urls.Home));
                _cursor.Center();
                DiagLog.Add("home screen shown");
            }
            catch (Exception ex)
            {
                DiagLog.Add("home screen failed: " + ex.Message);
                Navigate(Urls.Home);
            }
        }

        private void Navigate(string url)
        {
            _atHome = false;
            DiagLog.Add("navigate: " + url);
            _web.LoadUrl(url);
            _cursor.Center();
            UpdateStatus();
        }

        /// <summary>
        /// The one real speed-up available: the engine cannot be made faster, but it
        /// can be given much less to do.
        /// </summary>
        private void ToggleImages()
        {
            _imagesOn = !_imagesOn;
            Store.Set("images", _imagesOn);
            try
            {
                if (_web.Settings != null)
                {
                    _web.Settings.AutomaticImageLoadingAllowed = _imagesOn;
                }

                Flash(_imagesOn ? "Images on" : "Images off — faster");
                _web.Reload();
            }
            catch (Exception ex)
            {
                DiagLog.Add("image toggle failed: " + ex.Message);
            }
        }

        private void ToggleFavourite()
        {
            if (_atHome)
            {
                return;
            }

            bool kept = Store.ToggleFavourite(_cachedUrl, _cachedTitle);
            DiagLog.Add((kept ? "kept " : "removed ") + _cachedUrl);
            Flash(kept ? "Kept this page" : "Removed from favourites");
        }

        private void Flash(string message)
        {
            ShowChrome();
            _flashUntil = DateTime.UtcNow.AddSeconds(2.5);
            _status.TextColor = NuiTheme.Accent;
            _status.Text = message;
        }

        private void ApplyPreset(int index)
        {
            _presetIndex = index;
            UserAgentPreset preset = _presets[index];
            try
            {
                string ua = preset.Value ?? _engineUserAgentRaw;
                if (!string.IsNullOrEmpty(ua))
                {
                    _web.UserAgent = ua;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("UA set failed: " + ex.Message);
            }

            DiagLog.Add("UA preset: " + preset.Label);
            UpdateStatus();
        }

        private void ApplyViewportFix()
        {
            try
            {
                string script = _viewportFix
                    ? "window." + PageScript.Namespace + ".setViewport(" +
                      _window.WindowSize.Width.ToString(CultureInfo.InvariantCulture) + ")"
                    : "window." + PageScript.Namespace + ".clearViewport()";
                _web.EvaluateJavaScript(
                    "(function(){var a=String(" + script + ");var b=String(window." +
                    PageScript.Namespace + ".metrics());return a+' | '+b;})()",
                    result =>
                    {
                        if (!string.IsNullOrEmpty(result))
                        {
                            _lastMetrics = result;
                            DiagLog.Add("viewport: " + result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("viewport fix failed: " + ex.Message);
            }
        }

        private void Probe()
        {
            try
            {
                _web.EvaluateJavaScript(
                    "[navigator.userAgent, window.innerWidth + 'x' + window.innerHeight," +
                    " String(window.devicePixelRatio), document.title].join('  |  ')",
                    result =>
                    {
                        // Fires once empty before the real result arrives.
                        if (string.IsNullOrEmpty(result))
                        {
                            return;
                        }

                        _lastProbe = result;
                        DiagLog.Add("probe: " + _lastProbe);
                        UpdateStatus();
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("probe failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- reporting

        private void ShowOverlay(bool visible)
        {
            _overlayVisible = visible;
            if (visible)
            {
                _overlay.Text = Report();
                _overlay.Show();
                _overlay.RaiseToTop();
            }
            else
            {
                _overlay.Hide();
            }
        }

        private void UpdateStatus()
        {
            if (_web == null)
            {
                _status.TextColor = NuiTheme.Negative;
                _status.Text = "web engine unavailable";
                return;
            }

            _cachedUrl = SafeUrl();
            _cachedTitle = SafeTitle();
            _cachedGeometry = _web.Size.Width + "x" + _web.Size.Height +
                              "   (window " + _window.WindowSize.Width + "x" +
                              _window.WindowSize.Height + ")";

            string url = _atHome ? string.Empty : _cachedUrl;
            _host.Text = _atHome ? "Overscan" : HostOf(url);
            _path.Text = _atHome ? "start screen" : PathOf(url);

            if (_flashUntil != DateTime.MinValue)
            {
                return;
            }

            _status.TextColor = NuiTheme.InkMuted;
            _status.Text = (!_atHome && Store.IsFavourite(_cachedUrl) ? "★   ·   " : string.Empty) +
                           ShortPreset(_presets[_presetIndex].Label) +
                           "   ·   " + (_keysToPage ? "page keys" : "cursor") +
                           (_viewportFix ? "   ·   fit" : string.Empty) +
                           (_imagesOn ? string.Empty : "   ·   no images");
        }

        private string Report()
        {
            string engine = _web == null
                ? "engine    : FAILED — " + (_engineFailure ?? "(unknown)")
                : "engine UA : " + _engineUserAgent + "\n" +
                  "forced UA : " + (_presets[_presetIndex].Value ?? "(engine default)") + "\n" +
                  "page sees : " + _lastProbe + "\n" +
                  "view geom : " + _cachedGeometry + "\n" +
                  "page metr : " + _lastMetrics + "\n" +
                  "vp fix    : " + (_viewportFix ? "ON" : "off") + "  (key 6)\n" +
                  "frame click: " + NuiNativeTouch.LastResult + "\n" +
                  "url       : " + _cachedUrl;

            return "Overscan diagnostics (NUI build)\n\n" +
                   "platform  : NUI WebView, api-version 9.0+\n" +
                   engine + "\n\nlog\n" + DiagLog.Dump();
        }

        private static string ShortPreset(string label)
        {
            int bracket = label.IndexOf(" (", StringComparison.Ordinal);
            return bracket < 0 ? label : label.Substring(0, bracket);
        }

        private static string HostOf(string url)
        {
            string rest = url ?? string.Empty;
            int scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                rest = rest.Substring(scheme + 3);
            }

            int slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : rest;
        }

        private static string PathOf(string url)
        {
            string rest = url ?? string.Empty;
            int scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                rest = rest.Substring(scheme + 3);
            }

            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return string.Empty;
            }

            string path = rest.Substring(slash);
            return path.Length > 48 ? path.Substring(0, 45) + "…" : path;
        }

        private string SafeUserAgent()
        {
            try
            {
                string ua = _web.UserAgent;
                return string.IsNullOrEmpty(ua) ? null : ua;
            }
            catch (Exception ex)
            {
                DiagLog.Add("reading engine UA failed: " + ex.Message);
                return null;
            }
        }

        private string SafeUrl()
        {
            try
            {
                return _web.Url ?? "-";
            }
            catch (Exception)
            {
                return "(unreadable)";
            }
        }

        private string SafeTitle()
        {
            try
            {
                return string.IsNullOrEmpty(_web.Title) ? "(untitled)" : _web.Title;
            }
            catch (Exception)
            {
                return "(untitled)";
            }
        }
    }
}
