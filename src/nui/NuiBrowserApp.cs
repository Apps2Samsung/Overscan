using System;
using System.Globalization;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// The NUI build, for platforms where <c>Tizen.WebView</c> is gone.
    ///
    /// Tizen 10.0 (verified on the TV emulator) no longer ships
    /// Tizen.WebView.dll at all — the ElmSharp build dies with a
    /// FileNotFoundException the moment it touches the type. NUI's WebView is
    /// public API from API 9, so this build covers 9.0+ while the ElmSharp build
    /// covers 5.0-8.0. Everything engine-independent (user agents, the injected
    /// cursor script, diagnostics) is shared with it.
    /// </summary>
    internal sealed class NuiBrowserApp : NUIApplication
    {
        private const string HomeUrl = "https://duckduckgo.com/";
        private const int TopBarHeight = 64;

        private Window _window;
        private WebView _web;
        private TextLabel _status;
        private TextLabel _overlay;
        private NuiCursor _cursor;
        private NuiKeyboard _keyboard;

        private UserAgentPreset[] _presets = UserAgents.Defaults();
        private int _presetIndex;
        private string _engineUserAgentRaw;
        private string _engineUserAgent = "(not read yet)";
        private string _lastProbe = "(no page probed yet)";
        private string _engineFailure;

        /// <summary>
        /// Last URL, cached on the main thread. The diag server answers on its own
        /// thread and DALi objects are not thread-safe — reading _web.Url from
        /// there hangs the request (and risks taking the app with it), so the
        /// report may only ever read plain cached strings.
        /// </summary>
        private volatile string _cachedUrl = "-";
        private bool _overlayVisible;
        private bool _keysToPage;

        protected override void OnCreate()
        {
            base.OnCreate();
            DiagServer.ReportProvider = Report;

            _window = GetDefaultWindow();
            _window.BackgroundColor = Color.Black;
            DiagLog.Add("window " + _window.WindowSize.Width + "x" + _window.WindowSize.Height);

            _status = new TextLabel
            {
                Position2D = new Position2D(16, 12),
                Size2D = new Size2D(_window.WindowSize.Width - 32, TopBarHeight - 16),
                PointSize = 11,
                TextColor = Color.White,
                Text = "starting…",
            };
            _window.Add(_status);

            _overlay = new TextLabel
            {
                Position2D = new Position2D(48, TopBarHeight + 24),
                Size2D = new Size2D(_window.WindowSize.Width - 96, _window.WindowSize.Height - TopBarHeight - 72),
                PointSize = 9,
                TextColor = Color.White,
                BackgroundColor = new Color(0f, 0f, 0f, 0.92f),
                MultiLine = true,
                Text = string.Empty,
            };
            _overlay.Hide();
            _window.Add(_overlay);

            _keyboard = new NuiKeyboard(_window);
            _keyboard.Committed += OnKeyboardCommitted;

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

            ApplyPreset(0);
            Navigate(HomeUrl);
        }

        private bool TryStartEngine()
        {
            try
            {
                _web = new WebView
                {
                    Position = new Position(0, TopBarHeight),
                    Size = new Size(_window.WindowSize.Width, _window.WindowSize.Height - TopBarHeight),
                };
                _window.Add(_web);
                DiagLog.Add("NUI WebView created");

                _web.EnableJavaScript = true;
                if (_web.Settings != null)
                {
                    _web.Settings.JavaScriptEnabled = true;
                }

                _web.PageLoadFinished += (s, e) =>
                {
                    DiagLog.Add("load finished: " + SafeUrl());
                    _cursor.Reinstall();
                    Probe();
                    UpdateStatus();
                };
                _web.PageLoadError += (s, e) =>
                {
                    DiagLog.Add("load error on " + SafeUrl());
                    UpdateStatus();
                };

                _cursor = new NuiCursor(_web);
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
        /// Asks the page what it actually sees. NUI can take the result straight
        /// back through a callback, so unlike the ElmSharp build this needs no
        /// message-bridge object.
        /// </summary>
        private void Probe()
        {
            try
            {
                _web.EvaluateJavaScript(
                    "[navigator.userAgent, window.innerWidth + 'x' + window.innerHeight," +
                    " String(window.devicePixelRatio), document.title].join('  |  ')",
                    result =>
                    {
                        // The callback fires once with an empty string before the
                        // real result arrives; keeping that would blank the report.
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

        private void OnWindowKey(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down)
            {
                return;
            }

            string key = e.Key.KeyPressedName;

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
                    else
                    {
                        Exit();
                    }

                    break;

                case RemoteKeys.Num0:
                case RemoteKeys.More:
                case RemoteKeys.Menu:
                    _keyboard.Open(KeyboardTarget.Address, _cachedUrl == "-" ? string.Empty : _cachedUrl);
                    break;

                case RemoteKeys.Num2:
                    _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    break;

                case RemoteKeys.Num1:
                    ApplyPreset((_presetIndex + 1) % _presets.Length);
                    _web.Reload();
                    break;

                case RemoteKeys.Num3:
                    ShowOverlay(!_overlayVisible);
                    break;

                case RemoteKeys.Num4:
                    _keysToPage = !_keysToPage;
                    DiagLog.Add(_keysToPage ? "keys -> page" : "keys -> cursor");
                    UpdateStatus();
                    break;

                default:
                    DiagLog.Add("unhandled key: " + key);
                    break;
            }
        }

        private void OnKeyboardCommitted(string text, KeyboardTarget target)
        {
            if (target == KeyboardTarget.Address)
            {
                Navigate(Urls.Normalize(text));
                return;
            }

            // Type then Enter, in ONE evaluation on purpose. Two overlapping
            // EvaluateJavaScript-with-callback calls do not each get their own
            // result: the second handler received both replies and the first never
            // fired (observed as two "submit ->" lines, no "typed into" line), so
            // NUI appears to keep a single pending handler. Anything that needs two
            // results must combine them into one script.
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

        private void Navigate(string url)
        {
            DiagLog.Add("navigate: " + url);
            _web.LoadUrl(url);
            _cursor.Center();
            UpdateStatus();
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

        private void UpdateStatus()
        {
            if (_web == null)
            {
                _status.Text = "web engine unavailable — [3] log";
                return;
            }

            _cachedUrl = SafeUrl();
            _status.Text = _presets[_presetIndex].Label +
                           "   |   " + (_keysToPage ? "keys: page" : "keys: cursor") +
                           "   |   " + _cachedUrl +
                           "   |   [0] url  [1] UA  [2] type  [3] info  [4] keys";
        }

        private string Report()
        {
            string engine = _web == null
                ? "engine    : FAILED — " + (_engineFailure ?? "(unknown)")
                : "engine UA : " + _engineUserAgent + "\n" +
                  "forced UA : " + (_presets[_presetIndex].Value ?? "(engine default)") + "\n" +
                  "page sees : " + _lastProbe + "\n" +
                  "url       : " + _cachedUrl;

            return "Overscan diagnostics (NUI build)\n\n" +
                   "platform  : NUI WebView, api-version " +
                   9.ToString(CultureInfo.InvariantCulture) + ".0+\n" +
                   engine + "\n\nlog\n" + DiagLog.Dump();
        }
    }
}
