using System;
using System.Globalization;
using ElmSharp;
using Tizen.Applications;
using Tizen.WebView;

namespace Overscan
{
    /// <summary>
    /// A Chromium-EFL shell aimed at Samsung TVs: forces a desktop user agent,
    /// keeps JavaScript on, and drives a pointer from the remote's D-pad.
    ///
    /// Uses <c>Tizen.WebView</c> (ElmSharp/ewk) rather than the NUI WebView: the
    /// NUI one is public API only from TizenFX API 9, while Tizen.WebView has been
    /// public since API 4, so the same source builds for TV 5.0 through 8.0+.
    /// </summary>
    internal sealed class BrowserApp : CoreUIApplication
    {
        internal const string LogTag = "Overscan";

        private const string BridgeName = "sbnative";
        private const string HomeUrl = "https://duckduckgo.com/";
        private const int TopBarHeight = 68;

        private Window _window;
        private Box _mainBox;
        private Label _urlLabel;
        private ElmKeyboard _keyboard;
        private Label _status;
        private Label _diag;
        private Rectangle _diagBackdrop;
        private Label _hints;
        private Rectangle _hintsBackdrop;
        private bool _hintsVisible;
        private WebView _web;
        private VirtualCursor _cursor;

        private UserAgentPreset[] _presets = UserAgents.Defaults();
        private int _presetIndex;

        /// <summary>The TV's own UA, captured before we override anything.</summary>
        private string _engineUserAgent = "(not read yet)";

        /// <summary>Same value, but null when it could not be read.</summary>
        private string _engineUserAgentRaw;
        private string _lastProbe = "(no page probed yet)";
        private string _lastClick = "-";
        private bool _diagVisible;

        /// <summary>Set when chromium-efl or the view itself failed to come up.</summary>
        private string _engineFailure;

        /// <summary>
        /// URL and title cached on the main thread. DiagServer answers on its own
        /// thread, and EFL/ewk objects are no more thread-safe than DALi, so the
        /// report must never touch the live web view.
        /// </summary>
        private volatile string _cachedUrl = "-";
        private volatile string _cachedTitle = "(untitled)";
        private volatile string _cachedGeometry = "(unmeasured)";
        private volatile string _lastMetrics = "(not measured yet)";
        private bool _viewportFix;

        /// <summary>
        /// When true the remote's keys go to the page (so web forms and the
        /// engine's own spatial navigation work); when false we own them and drive
        /// the cursor.
        /// </summary>
        private bool _keysToPage;

        protected override void OnCreate()
        {
            base.OnCreate();

            DiagServer.ReportProvider = DiagnosticsText;
            DiagLog.Add("OnCreate: building UI");
            BuildUi();
            DiagLog.Add("OnCreate: UI built");

            // Bringing up chromium-efl is the one step we expect to be able to
            // fail: there are reports of an app-created Tizen.WebView crashing on
            // the TV emulator with nothing in the log, and the TV profile is not
            // what these bindings are mainly tested against. Failing here has to
            // produce a readable screen, not a vanished app — otherwise a device
            // test tells us nothing about *why* it did not work.
            // The call itself is guarded as well as the body: if Tizen.WebView is
            // missing from the platform entirely, the assembly-load failure is
            // raised while preparing TryStartEngine, which a try/catch *inside*
            // that method would never see.
            bool started;
            try
            {
                started = TryStartEngine();
            }
            catch (Exception ex)
            {
                _engineFailure = "loading Tizen.WebView failed — " + ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("ENGINE FAILURE " + _engineFailure);
                started = false;
            }

            if (!started)
            {
                ShowEngineFailure();
                return;
            }

            ConfigureWebView();

            // Read the engine default before overriding, then pick a desktop UA
            // whose Chrome version matches the engine we are actually running on.
            _engineUserAgentRaw = SafeUserAgent();
            _engineUserAgent = _engineUserAgentRaw ?? "(unavailable)";
            _presets[0] = UserAgents.MatchingEngine(_engineUserAgentRaw);
            DiagLog.Add("engine UA: " + _engineUserAgent);

            ApplyPreset(0);
            Navigate(HomeUrl);
        }

        protected override void OnTerminate()
        {
            // Deliberately NOT calling Chromium.Shutdown(): it is reported to hang
            // the app on exit (TizenFX issue 3274), and the process is going away
            // anyway, which tears the engine down with it.
            base.OnTerminate();
        }

        /// <summary>
        /// Initializes chromium-efl and creates the view. Returns false, with the
        /// reason recorded in <see cref="_engineFailure"/>, if either step fails.
        /// </summary>
        private bool TryStartEngine()
        {
            try
            {
                // Must come first on retail 5.0: without it Chromium.Initialize
                // cannot resolve libchromium-ewk.so at all. Proven on the RU7020.
                NativeEngine.Preload();

                DiagLog.Add("Chromium.Initialize()");
                int refCount = Chromium.Initialize();
                DiagLog.Add("Chromium initialized, refcount=" +
                            refCount.ToString(CultureInfo.InvariantCulture));

                _web = new WebView(_window)
                {
                    AlignmentX = -1,
                    AlignmentY = -1,
                    WeightX = 1,
                    WeightY = 1,
                };
                _web.Show();
                _mainBox.PackEnd(_web);
                // A focused web view lets the page raise the platform IME (and the
                // page's own autofocus then eats the remote). Keys stay with us
                // until key 4 hands them over deliberately.
                _web.SetFocus(false);
                DiagLog.Add("WebView created");

                _cursor = new VirtualCursor(_window, _web, BridgeName);
                _keyboard = new ElmKeyboard(_window);
                _keyboard.Committed += OnKeyboardCommitted;
                _web.KeyDown += OnKeyDown;
                return true;
            }
            catch (Exception ex)
            {
                _engineFailure = ex.GetType().Name + ": " + ex.Message;
                DiagLog.Add("ENGINE FAILURE " + _engineFailure);
                return false;
            }
        }

        private void ShowEngineFailure()
        {
            var message = new Label(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 1,
                WeightY = 1,
                LineWrapType = WrapType.Mixed,
            };
            message.Text = Markup(
                "The web engine did not start.\n\n" +
                _engineFailure + "\n\n" +
                "chromium-efl could not be initialized or the view could not be\n" +
                "created in this app. Press 3 for the full log, Back to exit.");
            message.Show();
            _mainBox.PackEnd(message);
            UpdateStatus();
        }

        private void BuildUi()
        {
            _window = new Window("Overscan")
            {
                AvailableRotations = DisplayRotation.Degree_0,
            };
            _window.Show();
            _window.Active();

            var background = new Background(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 1,
                WeightY = 1,
                Color = Color.FromRgb(18, 18, 20),
            };
            background.Show();
            _window.AddResizeObject(background);

            var conformant = new Conformant(_window);
            conformant.Show();

            var mainBox = new Box(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 1,
                WeightY = 1,
            };
            mainBox.Show();
            conformant.SetContent(mainBox);

            mainBox.PackEnd(BuildTopBar());
            _mainBox = mainBox;

            BuildDiagOverlay();
            BuildHints();

            _window.KeyDown += OnKeyDown;
            _window.BackButtonPressed += (s, e) => GoBackOrExit();

            foreach (string key in RemoteKeys.Grabbed)
            {
                try
                {
                    _window.KeyGrabEx(key);
                }
                catch (Exception ex)
                {
                    // Not every key exists on every TV; a failed grab just means
                    // that key stays with the system.
                    DiagLog.Add("keygrab " + key + " failed: " + ex.Message);
                }
            }
        }

        private Box BuildTopBar()
        {
            var topBar = new Box(_window)
            {
                AlignmentX = -1,
                AlignmentY = 0,
                WeightX = 1,
                WeightY = 0,
                IsHorizontal = true,
                MinimumHeight = TopBarHeight,
            };
            topBar.Show();

            // A Label, NOT an Entry: on a real TV an Entry grabs focus at startup
            // and the platform IME appears over the page and eats every remote key.
            // Text input goes through ElmKeyboard instead.
            _urlLabel = new Label(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 2,
                WeightY = 1,
                MinimumHeight = TopBarHeight,
            };
            _urlLabel.Show();

            _status = new Label(_window)
            {
                AlignmentX = -1,
                AlignmentY = -1,
                WeightX = 3,
                WeightY = 1,
                MinimumHeight = TopBarHeight,
            };
            _status.Show();

            topBar.PackEnd(_urlLabel);
            topBar.PackEnd(_status);
            return topBar;
        }

        /// <summary>
        /// Key hints live in the bottom-right corner, not in the top bar: at TV
        /// viewing distance a single line of everything is unreadable. Toggled with
        /// key 7, and off by default once you know the keys.
        /// </summary>
        private void BuildHints()
        {
            Size screen = _window.ScreenSize;
            int width = 430;
            int height = 250;

            _hintsBackdrop = new Rectangle(_window)
            {
                Color = Color.FromRgba(0, 0, 0, 190),
                Geometry = new Rect(screen.Width - width - 32, screen.Height - height - 32, width, height),
            };

            _hints = new Label(_window)
            {
                Geometry = new Rect(screen.Width - width - 16, screen.Height - height - 16,
                                    width - 32, height - 32),
                LineWrapType = WrapType.Mixed,
            };
            _hints.Text = Markup(
                "0  address bar\n" +
                "1  user agent\n" +
                "2  cursor style\n" +
                "3  diagnostics\n" +
                "4  keys to page\n" +
                "5  type in field\n" +
                "6  viewport fix\n" +
                "7  hide this");

            ShowHints(true);
        }

        private void ShowHints(bool visible)
        {
            _hintsVisible = visible;
            if (visible)
            {
                _hintsBackdrop.Show();
                _hints.Show();
                _hintsBackdrop.RaiseTop();
                _hints.RaiseTop();
            }
            else
            {
                _hints.Hide();
                _hintsBackdrop.Hide();
            }
        }

        private void BuildDiagOverlay()
        {
            Size screen = _window.ScreenSize;
            var area = new Rect(60, TopBarHeight + 40, Math.Max(600, screen.Width - 120),
                                Math.Max(400, screen.Height - TopBarHeight - 140));

            _diagBackdrop = new Rectangle(_window)
            {
                Color = Color.FromRgba(0, 0, 0, 232),
                Geometry = area,
            };

            _diag = new Label(_window)
            {
                Geometry = new Rect(area.X + 24, area.Y + 24, area.Width - 48, area.Height - 48),
                LineWrapType = WrapType.Mixed,
            };
        }

        private void ConfigureWebView()
        {
            Settings settings = _web.GetSettings();
            if (settings != null)
            {
                // The whole point: sites must get their normal scripted layout.
                // (Only members present since API 4 are used here, so the same
                // source compiles for the tizen50 build.)
                settings.JavaScriptEnabled = true;
                settings.LoadImageAutomatically = true;
            }

            _web.AddJavaScriptMessageHandler(BridgeName, OnBridgeMessage);

            _web.LoadStarted += (s, e) => DiagLog.Add("load started");
            _web.LoadFinished += (s, e) =>
            {
                DiagLog.Add("load finished: " + _web.Url);
                _cursor.Reinstall();
                _web.Eval(PageScript.Probe(BridgeName));
                ApplyViewportFix();
                ReportMetrics();
                UpdateStatus();
            };
            _web.LoadError += (s, e) =>
            {
                DiagLog.Add("load error " + e.Code + ": " + e.Description);
                UpdateStatus();
            };
            _web.UrlChanged += (s, e) =>
            {
                _urlLabel.Text = Markup(e.GetAsString());
                UpdateStatus();
            };
            _web.TitleChanged += (s, e) => UpdateStatus();

            Context context = _web.GetContext();
            CookieManager cookies = context == null ? null : context.GetCookieManager();
            if (cookies != null)
            {
                cookies.SetCookieAcceptPolicy(CookieAcceptPolicy.Always);
                cookies.SetPersistentStorage(DirectoryInfo.Data, CookiePersistentStorage.SqlLite);
            }
        }

        private void OnBridgeMessage(JavaScriptMessage message)
        {
            string body = message.GetBodyAsString() ?? string.Empty;
            string[] parts = body.Split(PageScript.FieldSeparator);

            switch (parts[0])
            {
                case "probe":
                    // ua, innerWidth, innerHeight, dpr, title, url
                    _lastProbe = string.Join("  |  ", parts, 1, parts.Length - 1);
                    DiagLog.Add("probe: " + (parts.Length > 1 ? parts[1] : "?"));
                    break;

                case "metrics":
                    _lastMetrics = parts.Length > 1 ? parts[1] : "?";
                    DiagLog.Add("page metrics: " + _lastMetrics);
                    break;

                case "viewport":
                    DiagLog.Add("viewport fix: " + (parts.Length > 1 ? parts[1] : "?"));
                    break;

                case "typed":
                    DiagLog.Add("typed: " + (parts.Length > 1 ? parts[1] : "?"));
                    break;

                case "click":
                    _lastClick = parts.Length > 1 ? parts[1] : "?";
                    DiagLog.Add("click -> " + _lastClick);

                    // The script marks text fields with a FIELD: prefix. Opening the
                    // grid here is the whole point: the field cannot be focused
                    // (that raises the IME), so this is how text gets into it.
                    if (_lastClick.StartsWith("FIELD:", StringComparison.Ordinal))
                    {
                        _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    }

                    break;

                default:
                    DiagLog.Add("bridge: " + body);
                    break;
            }

            UpdateStatus();
        }

        private void OnKeyDown(object sender, EvasKeyEventArgs e)
        {
            string key = e.KeyName;

            // With no engine, only the log and the way out still work.
            if (_web == null)
            {
                if (key == RemoteKeys.Num3)
                {
                    ToggleDiagnostics();
                }
                else if (key == RemoteKeys.Back)
                {
                    DiagLog.Add("exiting (no engine)");
                    Exit();
                }

                return;
            }

            // The keyboard owns the remote whenever it is up. OnHold marks the
            // event consumed in EFL terms — without it the page behind scrolled
            // while the D-pad was moving between letters.
            if (_keyboard != null && _keyboard.IsVisible && _keyboard.HandleKey(key))
            {
                e.Flags |= EvasEventFlag.OnHold;
                return;
            }

            if (!_keysToPage)
            {
                // We are driving the cursor: the page must not see these keys.
                e.Flags |= EvasEventFlag.OnHold;
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
                    GoBackOrExit();
                    break;

                case RemoteKeys.Menu:
                case RemoteKeys.Info:
                case RemoteKeys.Search:
                case RemoteKeys.Num0:
                    OpenAddressBar();
                    break;

                case RemoteKeys.Num1:
                    ApplyPreset((_presetIndex + 1) % _presets.Length);
                    _web.Reload();
                    break;

                case RemoteKeys.Num2:
                    _cursor.ToggleVisual();
                    break;

                case RemoteKeys.Num3:
                    ToggleDiagnostics();
                    break;

                case RemoteKeys.Num4:
                    ToggleKeyRouting();
                    break;

                case RemoteKeys.Num7:
                    ShowHints(!_hintsVisible);
                    break;

                case RemoteKeys.Num6:
                    _viewportFix = !_viewportFix;
                    DiagLog.Add("viewport fix " + (_viewportFix ? "ON" : "OFF"));
                    ApplyViewportFix();
                    ReportMetrics();
                    UpdateStatus();
                    break;

                case RemoteKeys.Num5:
                    _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    break;

                default:
                    DiagLog.Add("unhandled key: " + key);
                    UpdateStatus();
                    break;
            }
        }

        /// <summary>
        /// Forces the page's layout width to the view's real pixel width. ewk on 5.0
        /// otherwise lays out at a width of its own choosing and stretches the
        /// result into the view; SetScale and the zoom settings are API 6+.
        /// </summary>
        private void ApplyViewportFix()
        {
            if (_web == null)
            {
                return;
            }

            try
            {
                Rect area = _web.Geometry;
                string script = _viewportFix
                    ? "window." + PageScript.Namespace + ".setViewport(" +
                      area.Width.ToString(CultureInfo.InvariantCulture) + ")"
                    : "window." + PageScript.Namespace + ".clearViewport()";
                _web.Eval("try{window." + BridgeName + ".postMessage('viewport\u0001'+String(" +
                          script + "));}catch(e){}");
            }
            catch (Exception ex)
            {
                DiagLog.Add("viewport fix failed: " + ex.Message);
            }
        }

        private void ReportMetrics()
        {
            try
            {
                _web.Eval("try{window." + BridgeName + ".postMessage('metrics\u0001'+String(window." +
                          PageScript.Namespace + ".metrics()));}catch(e){}");
            }
            catch (Exception ex)
            {
                DiagLog.Add("metrics failed: " + ex.Message);
            }
        }

        private void OnKeyboardCommitted(string text, KeyboardTarget target)
        {
            if (target == KeyboardTarget.Address)
            {
                Navigate(text);
                return;
            }

            // Type + Enter in one script: see the NUI build's note about a single
            // pending result handler; keeping it to one call is simply safer.
            try
            {
                _web.Eval("try{window." + BridgeName + ".postMessage('typed\\u0001'+" +
                          "String(window." + PageScript.Namespace + ".type(" + Urls.JsString(text) + "))+" +
                          "', submit -> '+String(window." + PageScript.Namespace + ".submit()));}catch(e){}");
            }
            catch (Exception ex)
            {
                DiagLog.Add("typing failed: " + ex.Message);
            }
        }

        private void OpenAddressBar()
        {
            _keyboard.Open(KeyboardTarget.Address, _cachedUrl == "-" ? string.Empty : _cachedUrl);
        }

        private void ToggleKeyRouting()
        {
            _keysToPage = !_keysToPage;
            _web.SetFocus(_keysToPage);
            if (_keysToPage)
            {
                _cursor.Hide();
            }
            else
            {
                _cursor.Reinstall();
            }

            DiagLog.Add(_keysToPage ? "keys -> page" : "keys -> cursor");
            UpdateStatus();
        }

        private void ToggleDiagnostics()
        {
            _diagVisible = !_diagVisible;
            if (_diagVisible)
            {
                _diag.Text = Markup(DiagnosticsText());
                _diagBackdrop.Show();
                _diag.Show();
                _diagBackdrop.RaiseTop();
                _diag.RaiseTop();
            }
            else
            {
                _diag.Hide();
                _diagBackdrop.Hide();
            }
        }

        private void ApplyPreset(int index)
        {
            _presetIndex = index;
            UserAgentPreset preset = _presets[index];
            try
            {
                // A null preset value means "no override" — restore the string the
                // engine reported at startup rather than clearing it, since an
                // empty UA is not the same as the default one.
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

        private void Navigate(string input)
        {
            string url = Urls.Normalize(input);
            DiagLog.Add("navigate: " + url);
            _web.LoadUrl(url);
            _cursor.Center();
            UpdateStatus();
        }

        private void GoBackOrExit()
        {
            if (_diagVisible)
            {
                ToggleDiagnostics();
                return;
            }

            if (_web.CanGoBack())
            {
                _web.GoBack();
                return;
            }

            DiagLog.Add("exiting");
            Exit();
        }

        /// <summary>Reads the engine's UA, or null if the call is not usable here.</summary>
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

        private void UpdateStatus()
        {
            if (_web == null)
            {
                _status.Text = Markup("web engine unavailable   |   [3] log  [Back] exit");
                return;
            }

            string title = string.IsNullOrEmpty(_web.Title) ? "(untitled)" : _web.Title;
            _urlLabel.Text = Markup(_cachedUrl == "-" ? "(no page)" : _cachedUrl);
            _cachedTitle = title;
            _cachedUrl = _web.Url ?? "-";
            Rect geometry = _web.Geometry;
            Size screen = _window.ScreenSize;
            _cachedGeometry = geometry.Width + "x" + geometry.Height + " at " +
                              geometry.X + "," + geometry.Y + "   (window " +
                              screen.Width + "x" + screen.Height + ")";
            // Deliberately short: this is read from across a room.
            string line = _presets[_presetIndex].Label +
                          "   |   " + (_keysToPage ? "keys: page" : "cursor " + _cursor.Visual) +
                          (_viewportFix ? "   |   vp fix" : string.Empty);
            _status.Text = Markup(line);
        }

        private string DiagnosticsText()
        {
            if (_web == null)
            {
                return "Overscan diagnostics\n" +
                       "\n" +
                       "engine    : FAILED TO START\n" +
                       "reason    : " + (_engineFailure ?? "(unknown)") + "\n" +
                       "\n" +
                       "log\n" + DiagLog.Dump();
            }

            return "Overscan diagnostics\n" +
                   "\n" +
                   "engine UA : " + _engineUserAgent + "\n" +
                   "forced UA : " + (_presets[_presetIndex].Value ?? "(engine default)") + "\n" +
                   "page sees : " + _lastProbe + "\n" +
                   "view geom : " + _cachedGeometry + "\n" +
                   "page metr : " + _lastMetrics + "\n" +
                   "vp fix    : " + (_viewportFix ? "ON" : "off") + "  (key 6)\n" +
                   "last click: " + _lastClick + "\n" +
                   "cursor    : " + _cursor.Visual + ", keys -> " + (_keysToPage ? "page" : "cursor") + "\n" +
                   "title     : " + _cachedTitle + "\n" +
                   "url       : " + _cachedUrl + "\n" +
                   "\n" +
                   "log\n" + DiagLog.Dump();
        }

        /// <summary>
        /// ElmSharp labels render EFL markup, so text has to be escaped and line
        /// breaks turned into tags.
        /// </summary>
        private static string Markup(string text)
        {
            return "<color=#f0f0f0>" +
                   text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                       .Replace("\n", "<br/>") +
                   "</color>";
        }
    }
}
