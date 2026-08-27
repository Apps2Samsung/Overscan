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
        private TextLabel _hintsFooter;

        private RemoteMenu _menu;
        private View _menuPanel;
        private TextLabel[] _menuLabels;
        private TextLabel[] _menuShortcuts;
        private View _menuHighlight;

        private UserAgentPreset[] _presets = UserAgents.Defaults();
        private int _presetIndex;
        private string _engineUserAgentRaw;
        private string _engineUserAgent = "(not read yet)";
        private string _lastProbe = "(no page probed yet)";
        private string _lastMetrics = "(not measured yet)";
        private string _engineFailure;

        /// <summary>
        /// How long after a frame click the page is asked what it saw. The click
        /// leaves on another thread and has a server to start, a socket to open and
        /// three messages to exchange before the engine has even been asked to
        /// deliver anything, so this waits for the slow version of all of that —
        /// and is still short enough to be an answer about the press you just made.
        /// </summary>
        private const int FrameWitnessDelay = 1200;

        /// <summary>
        /// What the page reported after the last native tap: whether any real input
        /// arrived at all, and whether a cross-origin frame took focus. The feed
        /// itself cannot tell us either (issue #20), and these two facts are what
        /// separate "the touch never reached the engine" from "it reached the page
        /// and the frame ignored it".
        /// </summary>
        private volatile string _frameWitness = "(no frame clicked yet)";

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

        /// <summary>
        /// How many OK presses have arrived in a row without the key being let go.
        /// Tizen delivers a held key as repeated Down events, and counting them is
        /// the only way to notice a hold that cannot also break an ordinary press:
        /// the press still clicks the moment it arrives, exactly as before, and the
        /// menu opens on top of that if the button stays down. Waiting for the Up
        /// event instead would be tidier and would put every click in this browser
        /// at the mercy of a firmware delivering it.
        /// </summary>
        private int _okRepeats;
        private DateTime _lastOk = DateTime.MinValue;

        /// <summary>
        /// Presses of OK in a row before a hold is taken to mean "open the menu".
        /// Tizen's repeat starts after about half a second and runs at roughly ten
        /// a second, so this lands a little over a second into the hold — past any
        /// accidental double-press, short of feeling stuck.
        /// </summary>
        private const int OkHoldRepeats = 6;

        /// <summary>Longest gap that still counts as the same hold.</summary>
        private const int OkRepeatGapMs = 400;
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
            BuildMenu();

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
                PassTouchesThrough(_menuPanel);

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

                    // Whatever needed a debugging port, this is not it any more.
                    // See NuiInspector.Stop: the window it is open for should be
                    // the captcha, not the evening.
                    NuiInspectorInput.Reset();
                    NuiInspector.Stop(_web);
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
            int height = 560;

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

            // "hold OK" comes first and is not a number, deliberately. On a slim
            // remote it is the only row on this card the user can act on, and a
            // list that opens with nine things they cannot do reads as "this app
            // is not for your remote" (issue #27).
            string[][] rows =
            {
                new[] { "hold OK", "menu" },
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
                    Size2D = new Size2D(150, 38),
                    PointSize = 13,
                    TextColor = NuiTheme.Ink,
                    Text = rows[i][0],
                });
                _hints.Add(new TextLabel
                {
                    Position2D = new Position2D(NuiTheme.Pad + 150, y + 3),
                    Size2D = new Size2D(width - NuiTheme.Pad - 170, 34),
                    PointSize = 11,
                    TextColor = NuiTheme.InkMuted,
                    Text = rows[i][1],
                });
            }

            // Where an unrecognised button reports itself. A remote we have never
            // seen sends a name we do not answer to, and the user cannot open the
            // diagnostics screen to find out what it was — that screen is behind a
            // number key too. So the name lands here, on the card they can see.
            _hintsFooter = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, height - NuiTheme.Pad - 30),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), 28),
                PointSize = 9,
                TextColor = NuiTheme.InkMuted,
                Text = string.Empty,
            };
            _hints.Add(_hintsFooter);

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

        /// <summary>
        /// The on-screen menu: every function this browser has, in a list the
        /// D-pad can walk. It exists for remotes without a numpad (issue #27),
        /// where it is the only way in, and it shows each entry's number key
        /// beside it so it also teaches the shortcut to anyone who has one.
        /// </summary>
        private void BuildMenu()
        {
            _menu = new RemoteMenu(new[]
            {
                new RemoteMenu.Item(RemoteMenu.ActionAddress, "Go to address…", "0"),
                new RemoteMenu.Item(RemoteMenu.ActionHome, "Start screen", "9"),
                new RemoteMenu.Item(RemoteMenu.ActionBookmark, "Keep this page", "8"),
                new RemoteMenu.Item(RemoteMenu.ActionTypeInField, "Type in a field…", "2"),
                new RemoteMenu.Item(RemoteMenu.ActionIdentity, "Identify as…", "1"),
                new RemoteMenu.Item(RemoteMenu.ActionKeysToPage, "Send keys to page", "4"),
                new RemoteMenu.Item(RemoteMenu.ActionFitPage, "Fit page to screen", "6"),
                new RemoteMenu.Item(RemoteMenu.ActionImages, "Images on/off", "Info"),
                new RemoteMenu.Item(RemoteMenu.ActionHints, "Remote card on/off", "7"),
                new RemoteMenu.Item(RemoteMenu.ActionDiagnostics, "Diagnostics", "3"),
                new RemoteMenu.Item(RemoteMenu.ActionQuit, "Close Overscan", string.Empty),
            });

            Size2D screen = _window.WindowSize;
            const int RowHeight = 62;
            int width = 720;
            int height = (NuiTheme.Pad * 2) + 56 + (_menu.Count * RowHeight) + 44;

            _menuPanel = new View
            {
                Position2D = new Position2D((screen.Width - width) / 2, (screen.Height - height) / 2),
                Size2D = new Size2D(width, height),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            _menuPanel.Add(new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, NuiTheme.Pad),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), 36),
                PointSize = 13,
                TextColor = NuiTheme.Accent,
                Text = "MENU",
            });

            // Behind the rows, so a row's own text is never covered by it.
            _menuHighlight = new View
            {
                Position2D = new Position2D(NuiTheme.Pad - 8, NuiTheme.Pad + 56),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2) + 16, RowHeight - 6),
                BackgroundColor = NuiTheme.Accent,
                CornerRadius = 8f,
            };
            _menuPanel.Add(_menuHighlight);

            _menuLabels = new TextLabel[_menu.Count];
            _menuShortcuts = new TextLabel[_menu.Count];

            for (int i = 0; i < _menu.Count; i++)
            {
                RemoteMenu.Item item = _menu.ItemAt(i);
                int y = NuiTheme.Pad + 56 + (i * RowHeight);

                _menuLabels[i] = new TextLabel
                {
                    Position2D = new Position2D(NuiTheme.Pad, y + 6),
                    Size2D = new Size2D(width - (NuiTheme.Pad * 2) - 140, 42),
                    PointSize = 15,
                    TextColor = NuiTheme.Ink,
                    Text = item.Label,
                };
                _menuPanel.Add(_menuLabels[i]);

                _menuShortcuts[i] = new TextLabel
                {
                    Position2D = new Position2D(width - NuiTheme.Pad - 140, y + 10),
                    Size2D = new Size2D(140, 38),
                    PointSize = 11,
                    HorizontalAlignment = HorizontalAlignment.End,
                    TextColor = NuiTheme.InkMuted,
                    Text = item.Shortcut,
                };
                _menuPanel.Add(_menuShortcuts[i]);
            }

            _menuPanel.Add(new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, height - NuiTheme.Pad - 30),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), 28),
                PointSize = 10,
                TextColor = NuiTheme.InkMuted,
                Text = "up/down to choose  ·  OK to pick  ·  Return to close",
            });

            _window.Add(_menuPanel);
            _menuPanel.Hide();
        }

        private void ShowMenu(bool visible)
        {
            if (visible)
            {
                _menu.Open();
                DrawMenuSelection();
                _menuPanel.Show();
                _menuPanel.RaiseToTop();
                DiagLog.Add("menu opened");
            }
            else
            {
                _menu.Close();
                _menuPanel.Hide();
            }
        }

        private void DrawMenuSelection()
        {
            const int RowHeight = 62;
            _menuHighlight.Position2D = new Position2D(
                _menuHighlight.Position2D.X,
                NuiTheme.Pad + 56 + (_menu.SelectedIndex * RowHeight));

            for (int i = 0; i < _menuLabels.Length; i++)
            {
                bool picked = i == _menu.SelectedIndex;

                // On the accent bar, muted grey is unreadable and the accent itself
                // is invisible: both rows swap to the panel colour instead.
                _menuLabels[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.Ink;
                _menuShortcuts[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.InkMuted;
            }
        }

        /// <summary>
        /// Everything the browser can be asked to do, in one place, so a number key
        /// and a menu row cannot drift apart.
        /// </summary>
        private void RunAction(string id)
        {
            // A number key can fire an action with the menu still up, and two
            // panels fighting over the middle of the screen reads as a glitch.
            // Reaching here from the menu has already closed it, so this is only
            // ever the shortcut's doing.
            if (_menu.Visible)
            {
                ShowMenu(false);
            }

            switch (id)
            {
                case RemoteMenu.ActionAddress:
                    _keyboard.Open(KeyboardTarget.Address, _atHome ? string.Empty : _cachedUrl);
                    break;

                case RemoteMenu.ActionHome:
                    ShowHome();
                    break;

                case RemoteMenu.ActionBookmark:
                    ToggleFavourite();
                    break;

                case RemoteMenu.ActionTypeInField:
                    _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    break;

                case RemoteMenu.ActionIdentity:
                    ApplyPreset((_presetIndex + 1) % _presets.Length);
                    Store.Set("uaPreset", _presetIndex);
                    _web.Reload();
                    break;

                case RemoteMenu.ActionKeysToPage:
                    _keysToPage = !_keysToPage;
                    DiagLog.Add(_keysToPage ? "keys -> page" : "keys -> cursor");
                    Flash(_keysToPage ? "keys go to the page" : "keys move the pointer");
                    UpdateStatus();
                    break;

                case RemoteMenu.ActionFitPage:
                    _viewportFix = !_viewportFix;
                    Store.Set("viewportFix", _viewportFix);
                    DiagLog.Add("viewport fix " + (_viewportFix ? "ON" : "OFF"));
                    ApplyViewportFix();
                    UpdateStatus();
                    break;

                case RemoteMenu.ActionImages:
                    ToggleImages();
                    break;

                case RemoteMenu.ActionHints:
                    _hintsWanted = !_hintsWanted;
                    ShowHints(_hintsWanted);
                    Store.Set("hints", _hintsWanted);
                    break;

                case RemoteMenu.ActionDiagnostics:
                    ShowOverlay(!_overlayVisible);
                    break;

                case RemoteMenu.ActionQuit:
                    Exit();
                    break;
            }
        }

        /// <summary>
        /// The menu's own key handling, while it is up. Returns false for a key the
        /// menu does not want, which the caller then treats as normal — a number
        /// key still does its job with the menu open rather than being swallowed.
        /// </summary>
        private bool HandleMenuKey(string key)
        {
            switch (key)
            {
                case RemoteKeys.Up:
                    _menu.Move(-1);
                    DrawMenuSelection();
                    return true;

                case RemoteKeys.Down:
                    _menu.Move(1);
                    DrawMenuSelection();
                    return true;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    string id = _menu.Selected.Id;
                    ShowMenu(false);
                    RunAction(id);
                    return true;

                case RemoteKeys.Back:
                case RemoteKeys.Left:
                    ShowMenu(false);
                    return true;
            }

            if (RemoteKeys.IsMenuKey(key) || RemoteKeys.IsMediaKey(key))
            {
                ShowMenu(false);
                return true;
            }

            return false;
        }

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
                // The engine never started, so there is no page and no menu — but
                // the diagnostics screen is the whole reason someone is still
                // pressing buttons, and on a remote with no numpad key 3 cannot
                // get them there. Any menu button does instead.
                if (key == RemoteKeys.Num3 || RemoteKeys.IsMenuKey(key) || RemoteKeys.IsMediaKey(key))
                {
                    ShowOverlay(!_overlayVisible);
                }
                else if (key == RemoteKeys.Back)
                {
                    Exit();
                }

                return;
            }

            if (OkHoldConsumed(key))
            {
                return;
            }

            if (_menu.Visible && HandleMenuKey(key))
            {
                return;
            }

            if (RemoteKeys.IsMenuKey(key) || (RemoteKeys.IsMediaKey(key) && !_keysToPage))
            {
                ShowMenu(true);
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
                    RunAction(RemoteMenu.ActionAddress);
                    break;

                case RemoteKeys.Num1:
                    RunAction(RemoteMenu.ActionIdentity);
                    break;

                case RemoteKeys.Num2:
                    RunAction(RemoteMenu.ActionTypeInField);
                    break;

                case RemoteKeys.Num3:
                    RunAction(RemoteMenu.ActionDiagnostics);
                    break;

                case RemoteKeys.Num4:
                    RunAction(RemoteMenu.ActionKeysToPage);
                    break;

                case RemoteKeys.Num6:
                    RunAction(RemoteMenu.ActionFitPage);
                    break;

                case RemoteKeys.Num7:
                    RunAction(RemoteMenu.ActionHints);
                    break;

                case RemoteKeys.Num8:
                    RunAction(RemoteMenu.ActionBookmark);
                    break;

                case RemoteKeys.Num9:
                    RunAction(RemoteMenu.ActionHome);
                    break;

                case RemoteKeys.Info:
                    RunAction(RemoteMenu.ActionImages);
                    break;

                default:
                    NoteUnknownKey(key);
                    break;
            }
        }

        /// <summary>
        /// Watches OK for a hold, and opens the menu on one. Returns true when the
        /// press belongs to a hold and must go no further.
        /// </summary>
        /// <remarks>
        /// Two things have to stay true here. A single press must behave exactly as
        /// it always did — a hold is detected from the repeats that follow, never by
        /// delaying the press itself, so no click in this browser waits on a Up
        /// event some firmware may not send. And once the menu is up, the rest of
        /// the hold has to be thrown away: the presses still arriving are the same
        /// button the user has not let go of yet, and letting them through would
        /// pick the first row of the menu they were only just opening.
        /// </remarks>
        private bool OkHoldConsumed(string key)
        {
            if (key != RemoteKeys.Ok && key != RemoteKeys.OkKeypad)
            {
                _okRepeats = 0;
                return false;
            }

            DateTime now = DateTime.UtcNow;
            bool sameHold = (now - _lastOk).TotalMilliseconds <= OkRepeatGapMs;
            _lastOk = now;

            if (!sameHold)
            {
                // A press in its own right: it clicks, or it picks a menu row.
                _okRepeats = 0;
                return false;
            }

            if (_menu.Visible)
            {
                return true;
            }

            _okRepeats++;
            if (_okRepeats < OkHoldRepeats)
            {
                return false;
            }

            _okRepeats = 0;
            ShowMenu(true);
            return true;
        }

        /// <summary>
        /// A button we have no name for. It goes to the diagnostics log as before,
        /// and now also onto the hints card — the user who most needs to report an
        /// unknown key is the one whose remote cannot reach the diagnostics screen.
        /// </summary>
        private void NoteUnknownKey(string key)
        {
            DiagLog.Add("unhandled key: " + key);

            if (_hintsFooter != null)
            {
                _hintsFooter.Text = "unknown button: " + key;
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
                ClickThroughFrame(result);
            }
        }

        /// <summary>
        /// Clicks inside a cross-origin frame, through the engine rather than
        /// through the page or the platform under it.
        ///
        /// The point comes back with the script's own report — see
        /// <see cref="PageScript"/> — and is in the page's CSS pixels, which is the
        /// space the engine hit-tests in. Deriving it from the window instead would
        /// be right today and wrong the moment a page is zoomed.
        ///
        /// <see cref="NuiInspectorInput"/> is the way in; <see cref="NuiNativeTouch"/>
        /// stays as the fallback for a set where no inspector will start, on the
        /// principle that a click that probably does nothing still beats no click at
        /// all — but on the set in issue #20 it is the one that does nothing, four
        /// builds over.
        /// </summary>
        private void ClickThroughFrame(string report)
        {
            try
            {
                int x;
                int y;
                bool havePoint = PointIn(report, out x, out y);

                // Wiped before anything goes out, and not after: the click leaves on
                // another thread, and a witness cleared behind it would erase the
                // very event it was there to catch.
                _cursor.ClearNativeWitness();

                bool inspector = havePoint &&
                                 NuiInspector.Ensure(_web) != 0 &&
                                 NuiInspectorInput.Click(x, y);

                if (!inspector)
                {
                    // Window pixels, because the platform below the engine knows
                    // nothing about a page's coordinate space.
                    Size2D screen = _window.WindowSize;
                    int windowX = (int)Math.Round(_cursor.FractionX * screen.Width);
                    int windowY = (int)Math.Round(_cursor.FractionY * screen.Height);

                    if (!NuiNativeTouch.Click(_window, windowX, windowY))
                    {
                        Flash("This frame cannot be clicked");
                        return;
                    }
                }

                // And then ask the page what actually arrived. The click itself is
                // blind from out here — issue #20 reported "fed tap at 705,126" and
                // an unmoved captcha, which narrows nothing down — so the answer has
                // to come from the only place that can see real input: the page.
                _cursor.ReportNativeWitness(FrameWitnessDelay, witness =>
                {
                    _frameWitness = witness;
                    DiagLog.Add("frame witness: " + witness);

                    if (inspector && !NuiInspectorInput.Succeeded)
                    {
                        Flash("This frame cannot be clicked");
                    }
                });
            }
            catch (Exception ex)
            {
                DiagLog.Add("frame click failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The point out of a <c>FRAME:IFRAME@660,124</c> report. False for a build
        /// of the script old enough not to carry one, which then takes the native
        /// path and its window coordinates.
        /// </summary>
        private static bool PointIn(string report, out int x, out int y)
        {
            x = 0;
            y = 0;

            int at = report.IndexOf('@');
            int comma = report.IndexOf(',', at + 1);
            if (at < 0 || comma < 0)
            {
                return false;
            }

            return int.TryParse(report.Substring(at + 1, comma - at - 1),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                   int.TryParse(report.Substring(comma + 1),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
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
                  "frame click: " + NuiInspectorInput.LastResult + "\n" +
                  "frame saw  : " + _frameWitness + "\n" +
                  "native tap : " + NuiNativeTouch.LastResult + "\n" +
                  "inspector  : " + NuiInspector.LastResult + "\n" +
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
