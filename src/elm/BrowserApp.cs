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
        private Label _urlLabel;
        private ElmKeyboard _keyboard;
        private Label _status;
        private Label _diag;
        private Rectangle _diagBackdrop;
        private Label _hints;
        private Rectangle _hintsBackdrop;
        private Rectangle _hintsEdge;
        private bool _hintsVisible;
        private Rectangle _barBg;
        private Rectangle _barEdge;
        private Rectangle _progress;
        private bool _chromeVisible;
        private bool _loading;
        private int _marquee;
        private DateTime _lastActivity = DateTime.UtcNow;
        private DateTime _flashUntil = DateTime.MinValue;
        private bool _atHome;
        private bool _hintsWanted = true;
        private bool _imagesOn = true;
        private IntPtr _tick;
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
        /// False until OnCreate has finished. DiagServer answers on its own thread
        /// and can therefore render a report *during* start-up; without this it
        /// reported a view that does not exist yet as "FAILED TO START", which is
        /// how issue #14 ended up with a failure screenshot and no reason on it.
        /// </summary>
        private volatile bool _started;

        /// <summary>What the engine's own init reported, for the report.</summary>
        private string _engineInit = "(not reached)";

        /// <summary>The page to open at launch, or null for the start screen.</summary>
        private string _startupUrl;

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

            // Before the UI: the keyboard resolves its remembered layout the first
            // time KeyboardLayouts is touched, so a Store initialised after it was
            // built silently threw the user's layout choice away.
            Store.Init(DirectoryInfo.Data);
            Breadcrumbs.Drop("OnCreate: building UI");
            BuildUi();
            Breadcrumbs.Drop("OnCreate: UI built");

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
            Breadcrumbs.Drop("engine UA: " + _engineUserAgent);

            _viewportFix = Store.GetBool("viewportFix", false);
            _hintsWanted = Store.GetBool("hints", true);
            _startupUrl = Store.Get("startupUrl", null);
            ShowHints(_hintsWanted);
            if (Store.GetInt("cursorVisual", 0) == 1)
            {
                _cursor.ToggleVisual();
            }

            ApplyPreset(Math.Min(Store.GetInt("uaPreset", 0), _presets.Length - 1));

            // A start page, if one was set with the keyboard's `start` key. Asked
            // for in issue #15: a browser that always opens on its own start screen
            // makes you type the same address every single time.
            if (string.IsNullOrEmpty(_startupUrl))
            {
                ShowHome();
            }
            else
            {
                Breadcrumbs.Drop("opening start page " + _startupUrl);
                Navigate(_startupUrl);
            }

            _started = true;
            Breadcrumbs.Drop("OnCreate: complete");
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

                if (!InitializeChromium())
                {
                    return false;
                }

                // Full-window geometry, not a Box child: the page gets the whole
                // panel and the chrome floats over it, so nothing is letterboxed.
                Size screen = _window.ScreenSize;
                _web = new WebView(_window)
                {
                    Geometry = new Rect(0, 0, screen.Width, screen.Height),
                };
                _web.Show();
                // A focused web view lets the page raise the platform IME (and the
                // page's own autofocus then eats the remote). Keys stay with us
                // until key 4 hands them over deliberately.
                _web.SetFocus(false);
                Breadcrumbs.Drop("WebView created");

                // Every step from here gets its own breadcrumb. Issues #13 and #14
                // both produced a log that stopped dead at "WebView created" with
                // five uninstrumented calls after it, so the reports could not say
                // which one took the app down.
                _cursor = new VirtualCursor(_window, _web, BridgeName);
                Breadcrumbs.Drop("cursor built");
                _keyboard = new ElmKeyboard(_window);
                _keyboard.Committed += OnKeyboardCommitted;
                _keyboard.StartPageSet += OnStartPageSet;
                Breadcrumbs.Drop("keyboard built");
                _web.KeyDown += OnKeyDown;
                Breadcrumbs.Drop("key handler attached");
                return true;
            }
            catch (Exception ex)
            {
                _engineFailure = ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("ENGINE FAILURE " + _engineFailure);
                return false;
            }
        }

        /// <summary>
        /// Brings chromium-efl up, and insists that it actually came up.
        ///
        /// <c>ewk_init()</c> returns the engine's reference count, so a successful
        /// first call returns 1; it returns **0** from every one of its own error
        /// paths. That distinction is the whole of issues #13 and #14: on a Q80
        /// (5.5) and on a 2018 set the call returned 0, the old code logged the
        /// number and carried on, and the WebView built on a dead engine then wedged
        /// start-up until the launcher killed the app ~15 seconds later — an app
        /// that installs, launches and vanishes with nothing to read.
        ///
        /// So a zero is a failure. Before giving up it is worth one retry with the
        /// argument vector supplied, which the ewk samples all set and TizenFX
        /// never does; the engine leaves its count at zero after a failed init, so
        /// calling again genuinely re-runs it rather than just incrementing.
        /// </summary>
        private bool InitializeChromium()
        {
            Breadcrumbs.Drop("Chromium.Initialize()");
            int refCount = Chromium.Initialize();
            _engineInit = "refcount=" + refCount.ToString(CultureInfo.InvariantCulture);
            Breadcrumbs.Drop("Chromium initialized, " + _engineInit);

            if (refCount > 0)
            {
                return true;
            }

            string argv = NativeEngine.SetArguments();
            Breadcrumbs.Drop("engine init returned 0 — retrying (" + argv + ")");
            refCount = Chromium.Initialize();
            _engineInit = "refcount=0, then " + refCount.ToString(CultureInfo.InvariantCulture) +
                          " after " + argv;
            Breadcrumbs.Drop("Chromium re-initialized, refcount=" +
                             refCount.ToString(CultureInfo.InvariantCulture));

            if (refCount > 0)
            {
                return true;
            }

            _engineFailure =
                "the TV's web engine refused to start (ewk_init returned 0).\n\n" +
                "The engine library loaded" +
                (NativeEngine.LoadedFrom == null ? " by name" : " from " + NativeEngine.LoadedFrom) +
                ", so this is not the DRM permission wall — the package is signed\n" +
                "correctly and the engine is present, but the firmware would not\n" +
                "bring it up in an app process.\n\n" +
                "Retry with the argument vector set: " + argv;
            Breadcrumbs.Drop("ENGINE FAILURE ewk_init returned 0 twice");
            return false;
        }

        private void ShowEngineFailure()
        {
            Size screen = _window.ScreenSize;

            var backdrop = new Rectangle(_window)
            {
                Color = Theme.PanelDeep,
                Geometry = new Rect(0, 0, screen.Width, screen.Height),
            };
            backdrop.Show();

            var message = new Label(_window)
            {
                Geometry = new Rect(120, 200, screen.Width - 240, screen.Height - 400),
                LineWrapType = WrapType.Mixed,
            };
            message.Text =
                Theme.Text("The web engine did not start", 44, Theme.Ink, true) +
                Theme.Text("\n\n" + (_engineFailure ?? "(unknown)") + "\n\n", 26, Theme.InkMuted) +
                Theme.Text("engine init: " + _engineInit + "\n" +
                           "engine lib : " + (NativeEngine.LoadedFrom ?? "(not preloaded)") + "\n\n",
                           24, Theme.InkMuted) +
                Theme.Text("Press 3 for the full log, Back to exit.\n" +
                           "The same report is on http://<this TV>:8081", 28, Theme.Accent);
            message.Show();
            message.RaiseTop();
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
                Color = Color.FromRgb(10, 11, 14),
            };
            background.Show();
            _window.AddResizeObject(background);

            BuildChrome();
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

            // Drives the chrome's auto-hide and the loading marquee.
            _tick = EcoreMainloop.AddTimer(0.15, OnTick);
        }

        /// <summary>
        /// A translucent strip floating over the page: URL on the left, state on the
        /// right, a loading marquee along the bottom edge. It hides itself a few
        /// seconds after the last keypress so the page is unobstructed, which is
        /// what you want on a screen this size.
        ///
        /// A Label, never an Entry: on a real TV an Entry takes focus at startup and
        /// the platform IME appears over the page and eats every remote key. Text
        /// input goes through ElmKeyboard instead.
        /// </summary>
        private void BuildChrome()
        {
            Size screen = _window.ScreenSize;

            _barBg = new Rectangle(_window)
            {
                Color = Theme.Panel,
                Geometry = new Rect(0, 0, screen.Width, Theme.BarHeight),
            };

            _barEdge = new Rectangle(_window)
            {
                Color = Theme.Edge,
                Geometry = new Rect(0, Theme.BarHeight, screen.Width, 2),
            };

            int urlWidth = (screen.Width * 62 / 100) - Theme.Pad;
            _urlLabel = new Label(_window)
            {
                Geometry = new Rect(Theme.Pad, 10, urlWidth, Theme.BarHeight - 20),
            };

            _status = new Label(_window)
            {
                Geometry = new Rect(Theme.Pad + urlWidth, 10,
                                    screen.Width - urlWidth - (Theme.Pad * 2), Theme.BarHeight - 20),
            };

            _progress = new Rectangle(_window)
            {
                Color = Theme.Accent,
                Geometry = new Rect(0, Theme.BarHeight - 2, 0, 4),
            };

            ShowChrome();
        }

        /// <summary>Reveals the chrome and restarts its idle countdown.</summary>
        private void ShowChrome()
        {
            _lastActivity = DateTime.UtcNow;
            if (_chromeVisible)
            {
                return;
            }

            _chromeVisible = true;
            _barBg.Show();
            _barEdge.Show();
            _urlLabel.Show();
            _status.Show();
            RaiseChrome();
        }

        private void HideChrome()
        {
            _chromeVisible = false;
            _barBg.Hide();
            _barEdge.Hide();
            _urlLabel.Hide();
            _status.Hide();
            _progress.Hide();
        }

        private void RaiseChrome()
        {
            _barBg.RaiseTop();
            _barEdge.RaiseTop();
            _urlLabel.RaiseTop();
            _status.RaiseTop();
            if (_loading)
            {
                _progress.RaiseTop();
            }
        }

        /// <summary>
        /// Ecore timer: animates the loading marquee and hides the chrome when idle.
        /// A determinate progress bar is not possible here — LoadProgress is API 6+ —
        /// so a moving accent bar stands in for "something is happening", which
        /// matters when a page takes 17 seconds on a 2019 panel.
        /// </summary>
        private bool OnTick()
        {
            if (_loading && _chromeVisible)
            {
                Size screen = _window.ScreenSize;
                int span = screen.Width / 5;
                _marquee = (_marquee + (span / 4)) % (screen.Width + span);
                int x = _marquee - span;
                int width = span;
                if (x < 0)
                {
                    width += x;
                    x = 0;
                }

                if (width > 0)
                {
                    _progress.Geometry = new Rect(x, Theme.BarHeight - 2, width, 4);
                    _progress.Show();
                    _progress.RaiseTop();
                }
            }

            if (_flashUntil != DateTime.MinValue && DateTime.UtcNow > _flashUntil)
            {
                _flashUntil = DateTime.MinValue;
                UpdateStatus();
            }

            bool busy = _loading || (_keyboard != null && _keyboard.IsVisible) || _diagVisible;
            if (_chromeVisible && !busy &&
                DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(4))
            {
                HideChrome();
            }

            return true;
        }

        /// <summary>
        /// Key hints live in the bottom-right corner, not in the top bar: at TV
        /// viewing distance a single line of everything is unreadable. Toggled with
        /// key 7, and off by default once you know the keys.
        /// </summary>
        private void BuildHints()
        {
            Size screen = _window.ScreenSize;
            int width = 470;
            int height = 430;
            int left = screen.Width - width - 40;
            int top = screen.Height - height - 40;

            _hintsEdge = new Rectangle(_window)
            {
                Color = Theme.Edge,
                Geometry = new Rect(left - 2, top - 2, width + 4, height + 4),
            };

            _hintsBackdrop = new Rectangle(_window)
            {
                Color = Theme.PanelDeep,
                Geometry = new Rect(left, top, width, height),
            };

            _hints = new Label(_window)
            {
                Geometry = new Rect(left + Theme.Pad, top + Theme.Pad,
                                    width - (Theme.Pad * 2), height - (Theme.Pad * 2)),
                LineWrapType = WrapType.Mixed,
            };

            string[][ ] rows =
            {
                new[] { "0", "address bar" },
                new[] { "1", "identify as…" },
                new[] { "2", "pointer style" },
                new[] { "3", "diagnostics" },
                new[] { "4", "keys to page" },
                new[] { "5", "type in field" },
                new[] { "6", "fit page" },
                new[] { "7", "hide this" },
            };

            var text = new System.Text.StringBuilder();
            text.Append(Theme.Text("Remote", 26, Theme.Accent, true)).Append("<br/><br/>");
            foreach (string[] row in rows)
            {
                text.Append(Theme.Text(row[0] + "   ", 28, Theme.Ink, true))
                    .Append(Theme.Text(row[1], 26, Theme.InkMuted))
                    .Append("<br/>");
            }

            _hints.Text = text.ToString();
            ShowHints(true);
        }

        private void ShowHints(bool visible)
        {
            _hintsVisible = visible;
            if (visible)
            {
                _hintsEdge.Show();
                _hintsBackdrop.Show();
                _hints.Show();
                _hintsEdge.RaiseTop();
                _hintsBackdrop.RaiseTop();
                _hints.RaiseTop();
            }
            else
            {
                _hints.Hide();
                _hintsBackdrop.Hide();
                _hintsEdge.Hide();
            }
        }

        private void BuildDiagOverlay()
        {
            Size screen = _window.ScreenSize;
            var area = new Rect(60, TopBarHeight + 40, Math.Max(600, screen.Width - 120),
                                Math.Max(400, screen.Height - TopBarHeight - 140));

            _diagBackdrop = new Rectangle(_window)
            {
                Color = Theme.PanelDeep,
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
            Breadcrumbs.Drop("reading view settings");
            Settings settings = _web.GetSettings();
            if (settings != null)
            {
                // The whole point: sites must get their normal scripted layout.
                // (Only members present since API 4 are used here, so the same
                // source compiles for the tizen50 build.)
                settings.JavaScriptEnabled = true;
                _imagesOn = Store.GetBool("images", true);
                settings.LoadImageAutomatically = _imagesOn;
            }

            Breadcrumbs.Drop("attaching script bridge");
            _web.AddJavaScriptMessageHandler(BridgeName, OnBridgeMessage);

            _web.LoadStarted += (s, e) =>
            {
                DiagLog.Add("load started");
                _loading = true;
                ShowChrome();
            };
            _web.LoadFinished += (s, e) =>
            {
                DiagLog.Add("load finished: " + _web.Url);
                _loading = false;
                _progress.Hide();
                _cursor.Reinstall();
                _web.Eval(PageScript.Probe(BridgeName));
                ApplyViewportFix();
                ReportMetrics();
                Store.RecordVisit(_web.Url, _web.Title);
                UpdateStatus();
            };
            _web.LoadError += (s, e) =>
            {
                DiagLog.Add("load error " + e.Code + ": " + e.Description);
                _loading = false;
                _progress.Hide();
                UpdateStatus();
            };
            _web.UrlChanged += (s, e) =>
            {
                _urlLabel.Text = Markup(e.GetAsString());
                UpdateStatus();
            };
            _web.TitleChanged += (s, e) => UpdateStatus();

            Breadcrumbs.Drop("configuring cookies");
            Context context = _web.GetContext();
            CookieManager cookies = context == null ? null : context.GetCookieManager();
            if (cookies != null)
            {
                cookies.SetCookieAcceptPolicy(CookieAcceptPolicy.Always);
                cookies.SetPersistentStorage(DirectoryInfo.Data, CookiePersistentStorage.SqlLite);
            }

            Breadcrumbs.Drop("view configured");
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
                    else if (_lastClick.StartsWith("FRAME:", StringComparison.Ordinal))
                    {
                        // A cross-origin frame: script cannot reach inside it, so
                        // the click has to be a real one. This is the captcha case
                        // from issue #15.
                        ClickThroughFrame();
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
            ShowChrome();

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
                case RemoteKeys.Search:
                case RemoteKeys.Num0:
                    OpenAddressBar();
                    break;

                case RemoteKeys.Num1:
                    ApplyPreset((_presetIndex + 1) % _presets.Length);
                    Store.Set("uaPreset", _presetIndex);
                    _web.Reload();
                    break;

                case RemoteKeys.Num2:
                    _cursor.ToggleVisual();
                    Store.Set("cursorVisual", _cursor.Visual == CursorVisual.Native ? 1 : 0);
                    break;

                case RemoteKeys.Num3:
                    ToggleDiagnostics();
                    break;

                case RemoteKeys.Num4:
                    ToggleKeyRouting();
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

                case RemoteKeys.Num7:
                    _hintsWanted = !_hintsVisible;
                    ShowHints(_hintsWanted);
                    Store.Set("hints", _hintsWanted);
                    break;

                case RemoteKeys.Num6:
                    _viewportFix = !_viewportFix;
                    DiagLog.Add("viewport fix " + (_viewportFix ? "ON" : "OFF"));
                    Store.Set("viewportFix", _viewportFix);
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

        /// <summary>
        /// Loads the generated start screen. It is a page, not a native screen, so
        /// the pointer and everything else work on it unchanged.
        /// </summary>
        private void ShowHome()
        {
            try
            {
                _atHome = true;
                _web.LoadHtml(HomePage.Build(Store.AllFavourites, Store.RecentHistory, Urls.Home),
                              HomePage.BaseUrl);
                _cursor.Center();
                DiagLog.Add("home screen shown");
            }
            catch (Exception ex)
            {
                DiagLog.Add("home screen failed: " + ex.Message);
                Navigate(Urls.Home);
            }
        }

        /// <summary>
        /// Images off is the single biggest speed-up available on an old set — the
        /// engine cannot be made faster, but it can be given much less to do.
        /// </summary>
        private void ToggleImages()
        {
            _imagesOn = !_imagesOn;
            Store.Set("images", _imagesOn);
            try
            {
                Settings settings = _web.GetSettings();
                if (settings != null)
                {
                    settings.LoadImageAutomatically = _imagesOn;
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

        /// <summary>
        /// Briefly replaces the status text. There is no notification surface on a TV,
        /// and an action with no feedback feels broken.
        /// </summary>
        private void Flash(string message)
        {
            ShowChrome();
            _flashUntil = DateTime.UtcNow.AddSeconds(2.5);
            _status.Text = Theme.Text(message, 28, Theme.Accent, true, "right");
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

        /// <summary>
        /// Feeds a real mouse click at the cursor, for a frame the injected script
        /// cannot see into. The cursor's position is a fraction of the viewport, and
        /// the page is painted into the view, so the same fraction of the view's
        /// geometry is the point on the canvas.
        /// </summary>
        private void ClickThroughFrame()
        {
            try
            {
                Rect area = _web.Geometry;
                int x = area.X + (int)Math.Round(_cursor.FractionX * area.Width);
                int y = area.Y + (int)Math.Round(_cursor.FractionY * area.Height);

                if (!NativeMouse.Click(_web, x, y))
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
        /// Remembers what was typed as the page to open at launch. Issue #15: the
        /// browser opening on its own start screen every time means retyping the
        /// same address on a remote control, which is nobody's idea of a good time.
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
                _diag.Text = Theme.Text(DiagnosticsText(false), 20, Theme.Ink);
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
            _atHome = false;
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

            if (!_atHome)
            {
                ShowHome();
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
                _status.Text = Theme.Text("web engine unavailable", 28, Theme.Negative, true, "right");
                return;
            }

            string title = string.IsNullOrEmpty(_web.Title) ? "(untitled)" : _web.Title;
            _cachedTitle = title;
            _cachedUrl = _web.Url ?? "-";
            Rect geometry = _web.Geometry;
            Size screen = _window.ScreenSize;
            _cachedGeometry = geometry.Width + "x" + geometry.Height + " at " +
                              geometry.X + "," + geometry.Y + "   (window " +
                              screen.Width + "x" + screen.Height + ")";

            _urlLabel.Text = FormatUrl(_atHome ? "-" : _cachedUrl);

            if (_flashUntil != DateTime.MinValue)
            {
                return;
            }


            // Right-hand side stays terse: this is read from across a room.
            string state = (!_atHome && Store.IsFavourite(_cachedUrl) ? "★   ·   " : string.Empty) +
                           ShortPreset(_presets[_presetIndex].Label) +
                           "   ·   " + (_keysToPage ? "page keys" : "cursor") +
                           (_viewportFix ? "   ·   fit" : string.Empty) +
                           (_imagesOn ? string.Empty : "   ·   no images");
            _status.Text = Theme.Text(state, 24, Theme.InkMuted, false, "right");
        }

        /// <summary>
        /// Host emphasised, the rest dimmed — at TV distance a full URL in one
        /// weight is a grey smear, and the host is the part that identifies where
        /// you are.
        /// </summary>
        private static string FormatUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url == "-")
            {
                return Theme.Text("Overscan", 32, Theme.InkMuted, true);
            }

            string rest = url;
            string scheme = string.Empty;
            int schemeAt = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeAt > 0)
            {
                scheme = url.Substring(0, schemeAt + 3);
                rest = url.Substring(schemeAt + 3);
            }

            int slash = rest.IndexOf('/');
            string host = slash < 0 ? rest : rest.Substring(0, slash);
            string path = slash < 0 ? string.Empty : rest.Substring(slash);

            if (path.Length > 60)
            {
                path = path.Substring(0, 57) + "…";
            }

            return Theme.Text(scheme, 22, Theme.InkMuted) +
                   Theme.Text(host, 32, Theme.Ink, true) +
                   Theme.Text(path, 24, Theme.InkMuted);
        }

        /// <summary>"Desktop Chrome 63 (engine-matched)" -> "Desktop Chrome 63".</summary>
        private static string ShortPreset(string label)
        {
            int bracket = label.IndexOf(" (", StringComparison.Ordinal);
            return bracket < 0 ? label : label.Substring(0, bracket);
        }

        /// <summary>
        /// The report served on :8081 and drawn by key 3.
        ///
        /// Called from the DiagServer thread while the main thread may be anywhere
        /// in start-up, so **every field it touches must be null-checked**: reading
        /// _cursor.Visual mid-start-up is what made the report itself throw a
        /// NullReferenceException in issue #13, losing the one piece of evidence
        /// the user had. It also may only read cached strings, never the live view.
        /// </summary>
        private string DiagnosticsText()
        {
            return DiagnosticsText(true);
        }

        /// <summary>
        /// <paramref name="full"/> is false for the on-screen overlay, which has a
        /// fixed box to draw in and cannot show the previous run's whole trail.
        /// </summary>
        private string DiagnosticsText(bool full)
        {
            string trail = full
                ? "\nprevious run (last line is where it died)\n" + Breadcrumbs.Previous +
                  "\nthis run\n" + DiagLog.Dump()
                : "\nlog\n" + DiagLog.Tail(10);

            if (_web == null)
            {
                // "Still starting" and "failed" look identical from this thread
                // unless we say which it is — issue #14's report said FAILED TO
                // START with reason (unknown) while start-up was merely in flight.
                bool failed = _engineFailure != null;
                return "Overscan diagnostics\n" +
                       "\n" +
                       "engine    : " + (failed ? "FAILED TO START" : "still starting") + "\n" +
                       "reason    : " + (_engineFailure ?? "(no failure recorded yet)") + "\n" +
                       "engine init: " + _engineInit + "\n" +
                       "engine lib : " + (NativeEngine.LoadedFrom ?? "(not preloaded)") + "\n" +
                       "trail file : " + Breadcrumbs.Location + "\n" +
                       trail;
            }

            UserAgentPreset preset =
                _presets != null && _presetIndex >= 0 && _presetIndex < _presets.Length
                    ? _presets[_presetIndex]
                    : null;
            string forced = preset == null
                ? "(not chosen yet)"
                : preset.Value ?? "(engine default)";

            return "Overscan diagnostics\n" +
                   "\n" +
                   "state     : " + (_started ? "running" : "still starting") + "\n" +
                   "engine UA : " + _engineUserAgent + "\n" +
                   "engine init: " + _engineInit + "\n" +
                   "forced UA : " + forced + "\n" +
                   "page sees : " + _lastProbe + "\n" +
                   "view geom : " + _cachedGeometry + "\n" +
                   "page metr : " + _lastMetrics + "\n" +
                   "vp fix    : " + (_viewportFix ? "ON" : "off") + "  (key 6)\n" +
                   "last click: " + _lastClick + "\n" +
                   "frame click: " + NativeMouse.LastResult + "\n" +
                   "start page: " + (string.IsNullOrEmpty(_startupUrl) ? "(start screen)" : _startupUrl) + "\n" +
                   "cursor    : " + (_cursor == null ? "(not built)" : _cursor.Visual.ToString()) +
                   ", keys -> " + (_keysToPage ? "page" : "cursor") + "\n" +
                   "title     : " + _cachedTitle + "\n" +
                   "url       : " + _cachedUrl + "\n" +
                   trail;
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
