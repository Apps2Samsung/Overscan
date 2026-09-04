using System;
using System.Collections.Generic;
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

        /// <summary>
        /// The heartbeat, held in a field for exactly the reason
        /// <see cref="NuiLater"/> keeps its own list: a NUI Timer with no live
        /// reference is collected, and this one used to be a local in
        /// <see cref="OnCreate"/>. It ticked until the first garbage collection —
        /// which a page load reliably causes — and then stopped, silently, taking
        /// the blank-view watchdog, the memory sampling and the deferred video path
        /// with it. Issue #20's trails are what that looks like from a TV: the
        /// `memory:` lines stop the moment pages start loading and never resume, the
        /// report shows a peak *below* the resident size because nothing is
        /// updating it any more, and a stored video preference is still waiting to
        /// be applied when the run ends. The ElmSharp build has always kept its
        /// equivalent in `_tick`; this one had not.
        /// </summary>
        private Timer _tick;

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
        /// How long a load may take to *begin* before the view is called dead. Not
        /// how long a page may take to arrive — PageLoadStarted fires as soon as the
        /// engine accepts the navigation, well before a byte is fetched, so this is
        /// generous even on a set that takes seven seconds to open DuckDuckGo.
        /// </summary>
        private const int BlankViewSeconds = 6;

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
        private bool _adBlockOn = true;
        private bool _videoOverlay = true;

        /// <summary>
        /// A stored video path that has been read but not yet handed to the
        /// engine. False on an install where the key has never been chosen, so
        /// such a set is never told anything at all and stays exactly where it
        /// was before the toggle existed.
        /// </summary>
        private volatile bool _videoPathPending;

        /// <summary>
        /// Whether `5` has ever been pressed on this install. Cached rather than
        /// asked of <see cref="Store"/>, because the report is built on
        /// DiagServer's thread and nothing there may touch the main thread's state.
        /// </summary>
        private volatile bool _videoPathChosen;
        private bool _pageEverLoaded;

        /// <summary>
        /// When a load was last asked for, cleared the moment the engine says it
        /// started one. See <see cref="CheckSomethingLoaded"/>: a load that is
        /// never even *begun* is the one failure this app could not see, and the
        /// black screen on issue #20 was it.
        /// </summary>
        private DateTime _loadAskedAt = DateTime.MinValue;

        /// <summary>What that load was for, so a recovery retries it and not home.</summary>
        private string _loadAskedFor;
        private int _blankRecoveries;

        /// <summary>
        /// How big the start screen was the last time it was built, for the report.
        /// Issue #53's black screen was a start screen that had grown past what
        /// the engine will load, and nothing on the report said how large the page
        /// it was refusing had got. Volatile for the same reason as
        /// <see cref="_blankState"/>.
        /// </summary>
        private volatile string _startPageState = "(not built yet)";

        /// <summary>
        /// What the blank-view watchdog has had to do, for the report. Volatile
        /// because DiagServer answers on its own thread, and a plain string is the
        /// only kind of state it is allowed to read.
        /// </summary>
        private volatile string _blankState = "(never blank)";
        private string _cookieState = "(not configured)";
        private bool _keysToPage;
        private bool _viewportFix;
        private bool _atHome;
        private bool _loading;
        private bool _chromeVisible;
        private int _marquee;
        private DateTime _lastActivity = DateTime.UtcNow;

        /// <summary>
        /// When the trail was last told how big this process had become. See
        /// <see cref="ProcessMemory"/>: an app the low-memory killer takes away
        /// and an app that crashes leave the same silence behind them, and this
        /// line is what tells the two apart afterwards.
        /// </summary>
        private DateTime _lastMemoryNote = DateTime.MinValue;
        private long _peakMemoryMb;

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

        /// <summary>
        /// How often this process's size goes on the trail while a video is playing.
        /// It was five seconds throughout, and five seconds is what cost issue #20
        /// the resolution that mattered: the reading before the death was already
        /// twenty-five megabytes down, so whatever happened had happened by then and
        /// there was nothing in between.
        /// </summary>
        private const int MemorySecondsWithVideo = 2;

        /// <summary>
        /// And how often otherwise. Two seconds everywhere would double the length
        /// of a trail that goes into a report somebody has to copy out of a browser
        /// on their phone, to buy resolution on pages where nothing is happening.
        /// </summary>
        private const int MemorySecondsIdle = 5;

        /// <summary>
        /// How much the engine may write to stdout/stderr before the capture is
        /// given up. Generous for a crash message, small enough that a chatty
        /// firmware cannot fill somebody's television.
        /// </summary>
        private const long StdErrLimitBytes = 1024 * 1024;

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
            WatchTheWindow();

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

            _tick = new Timer(150);
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();
        }

        /// <summary>
        /// Records the things that happen to this app rather than inside it.
        ///
        /// The trail from issue #20's `build-c0cd5ab` shows a run that ends with the
        /// process's memory dropping by twenty-five megabytes in the last reading
        /// before it goes. Something released a lot at once, and there is a reading
        /// of that which has nothing to do with a crash: a window that stops being
        /// visible makes the engine give up its graphics resources, and an app that
        /// has been put in the background is one the platform may then close. From
        /// the sofa "I pressed something and Overscan disappeared" and "Overscan
        /// crashed" are the same sentence, and these lines are what tell them apart.
        /// See <see cref="NuiDeathWatch"/> for the rest of the ladder.
        /// </summary>
        private void WatchTheWindow()
        {
            try
            {
                _window.FocusChanged += delegate (object sender, Window.FocusChangedEventArgs e)
                {
                    Breadcrumbs.DropToTrail("window focus " + (e.FocusGained ? "gained" : "LOST"));
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("focus watch failed: " + ex.Message);
            }

            try
            {
                _window.VisibilityChanged += delegate (object sender, Window.VisibilityChangedEventArgs e)
                {
                    Breadcrumbs.DropToTrail("window " + (e.Visibility ? "visible" : "HIDDEN"));
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("visibility watch failed: " + ex.Message);
            }

            try
            {
                LowMemory += delegate (object sender, Tizen.Applications.LowMemoryEventArgs e)
                {
                    // The platform's own warning, and the last thing an app gets
                    // before the resource manager stops asking. The memory readings
                    // say this is not what is happening here — which is worth being
                    // able to state rather than assume. NUI has a MemoryLow signal of
                    // its own but keeps it internal; this is the app-framework one,
                    // inherited from CoreApplication, and it is public.
                    Breadcrumbs.DropToTrail("PLATFORM SAYS MEMORY IS LOW: " + e.LowMemoryStatus);
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("low-memory watch failed: " + ex.Message);
            }
        }

        protected override void OnPause()
        {
            Breadcrumbs.Drop("OnPause — this app is in the background now");
            base.OnPause();
        }

        protected override void OnResume()
        {
            Breadcrumbs.Drop("OnResume");
            base.OnResume();
        }

        protected override void OnTerminate()
        {
            // The one line that separates "the platform closed us" from "the process
            // died": a termination runs this, a crash never gets here.
            Breadcrumbs.Drop("OnTerminate — closing normally");
            base.OnTerminate();
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
                Breadcrumbs.Drop("NUI WebView created");

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

                    // Nothing this browser does is worth a private window, and the
                    // engine's own default for it is not documented anywhere we can
                    // read. With it on, cookies and local storage live in memory and
                    // die with the process — which is what a site logging you out
                    // every launch (issue #20) looks like from the sofa.
                    _web.Settings.PrivateBrowsingEnabled = false;
                }

                ConfigureCookies();

                // Before the first load, so the start screen's tiles are the first
                // requests it sees. Default on: a feature that is off by default is
                // a feature only the people who already know about it have.
                _adBlockOn = Store.GetBool("adblock", true);
                Breadcrumbs.Drop("installing request filter");
                NuiAdBlock.Install(_web, _adBlockOn);
                DiagLog.Add("ad block: " + NuiAdBlock.LastResult);

                // Read here, applied later. See ApplyVideoPath: on this set a
                // stored overlay handed to a WebView that has never loaded
                // anything gives a view that never loads anything either, so
                // the preference waits for a page to have gone through.
                _videoOverlay = Store.GetBool("videoOverlay", true);
                _videoPathChosen = Store.Get("videoOverlay", null) != null;

                // Applied whether or not anybody chose it, which is the change
                // `build-e78c0bc`'s trail argues for. Leaving the property alone was
                // meant to be the modest option — the engine's own default rather
                // than an opinion about every set — but that trail is the first one
                // showing what the engine's own default actually does here, and it
                // is not a third opinion, it is a broken path: video decodes into a
                // `fakesink` and the render thread segfaults on the fourth decoder,
                // while the two paths we can name both survive. An untouched engine
                // is the only configuration on this set that crashes, so it stops
                // being the one a fresh install lands in.
                _videoPathPending = true;

                _web.PageLoadStarted += (s, e) =>
                {
                    // On the trail, not only in the log: when a page takes the app
                    // down with it, the address it was opening is the first thing
                    // anyone will want to know.
                    Breadcrumbs.Drop("load started: " + SafeUrl());
                    _loading = true;

                    // The engine answered, so the view is not the dead kind.
                    _loadAskedAt = DateTime.MinValue;
                    if (_blankRecoveries > 0)
                    {
                        // Kept, not cleared: which rung got this view loading again
                        // is the whole answer issue #20 is waiting for, and it would
                        // otherwise be erased by the load that proves it worked.
                        _blankState = "recovered after " + _blankRecoveries +
                                      (_blankRecoveries == 1 ? " attempt" : " attempts");
                        _blankRecoveries = 0;
                    }
                    ShowChrome();

                    // Whatever needed a debugging port, this is not it any more.
                    // See NuiInspector.Stop: the window it is open for should be
                    // the captcha, not the evening.
                    NuiInspectorInput.Reset();
                    NuiInspector.Stop(_web);
                    NuiMediaWatch.Reset();
                    NuiVideoCap.Reset();
                    NuiVideoRect.Reset();
                };

                _web.PageLoadFinished += (s, e) =>
                {
                    _loading = false;
                    _pageEverLoaded = true;
                    _progress.Hide();
                    Breadcrumbs.Drop("load finished: " + SafeUrl() + "  [" + ProcessMemory.Summary() + "]");
                    _cursor.Reinstall();
                    InstallMediaWatch();
                    Probe();
                    ApplyViewportFix();
                    Store.RecordVisit(SafeUrl(), SafeTitle());
                    UpdateStatus();
                };

                _web.PageLoadError += (s, e) =>
                {
                    _loading = false;
                    _progress.Hide();
                    Breadcrumbs.Drop("load error on " + SafeUrl());
                    UpdateStatus();
                };

                WatchTheEngine();

                _cursor = new NuiCursor(_web);
                _cursor.Clicked += OnPageClicked;
                Breadcrumbs.Drop("engine ready");

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
        /// Listens to the page's console, which is the one thing the engine says
        /// about itself that this app has never read.
        ///
        /// It is the return channel for <see cref="NuiMediaWatch"/> — see there for
        /// why a <c>console.log</c> is used in preference to an evaluation — and it
        /// also carries the engine's own errors, which is where a media pipeline
        /// says it has failed. The fullscreen signals would have been worth having
        /// here too, and are not available: NUI declares them as internal callbacks
        /// rather than events at API 9.
        /// </summary>
        private void WatchTheEngine()
        {
            try
            {
                _web.ConsoleMessageReceived += delegate (object sender, WebViewConsoleMessageReceivedEventArgs e)
                {
                    try
                    {
                        WebConsoleMessage message = e.ConsoleMessage;
                        if (message != null)
                        {
                            NuiMediaWatch.Console(message.Level.ToString(), message.Text);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A diagnostic channel is not worth an exception on the
                        // engine's own callback thread.
                        DiagLog.Add("console message unreadable: " + ex.Message);
                    }
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("console watch failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Puts the media census, the decoder cap and the geometry probe into the
        /// page. Alongside the cursor's own script and for the same reason it is
        /// re-run on every load: a navigation takes the previous page's copy with it.
        ///
        /// Three evaluations rather than one, so that a page which somehow breaks one
        /// of them still gets the others — the census in particular, which is what
        /// would explain the breakage.
        /// </summary>
        private void InstallMediaWatch()
        {
            try
            {
                _web.EvaluateJavaScript(NuiMediaWatch.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("media watch failed: " + ex.Message);
            }

            try
            {
                _web.EvaluateJavaScript(NuiVideoCap.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("video cap failed: " + ex.Message);
            }

            try
            {
                _web.EvaluateJavaScript(NuiVideoRect.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("video rect failed: " + ex.Message);
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

        /// <summary>
        /// Gives the session somewhere to live that outlasts the process.
        ///
        /// Without this the engine keeps cookies in memory only, so closing
        /// Overscan logs you out of everything and every launch is a browser that
        /// has never been anywhere (issue #20). The ElmSharp build has done this
        /// since its first release, by way of <c>Context.GetCookieManager</c>; the
        /// NUI WebView hangs the same manager off the view itself and this build
        /// simply never asked it for one.
        ///
        /// The path is the app's own data directory — the only place a sideloaded
        /// app on a retail TV is allowed to write, and where the trail and the
        /// favourites already are.
        /// </summary>
        private void ConfigureCookies()
        {
            try
            {
                WebCookieManager cookies = _web.CookieManager;
                if (cookies == null)
                {
                    _cookieState = "engine offered no cookie manager";
                    DiagLog.Add("cookies: " + _cookieState);
                    return;
                }

                cookies.CookieAcceptPolicy = WebCookieManager.CookieAcceptPolicyType.Always;
                cookies.SetPersistentStorage(DirectoryInfo.Data, WebCookieManager.CookiePersistentStorageType.SqlLite);

                _cookieState = "kept in " + DirectoryInfo.Data;
                Breadcrumbs.Drop("cookies persistent: " + DirectoryInfo.Data);
            }
            catch (Exception ex)
            {
                _cookieState = "FAILED — " + ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("cookies: " + _cookieState);
            }
        }

        /// <summary>
        /// Chooses how video reaches the screen, and remembers the choice.
        ///
        /// A TV decodes video on hardware and shows it on an overlay plane, with a
        /// transparent hole punched through the page where the picture belongs.
        /// That is the right arrangement for the set's own browser, which owns the
        /// screen, and a questionable one for an app whose window DALi composites
        /// itself — so this was made a toggle, with the in-page path as the default,
        /// on the theory that a reel feed asking for one plane per video is what
        /// killed Overscan on Instagram (issue #20).
        ///
        /// **The theory was wrong and the default was a mistake.** The reporter's
        /// reels stopped crashing in the same build, but they came back *black*, and
        /// pressing `5` for the overlay is what made them play. The crash it was
        /// meant to fix was the memory-only profile beside it — see
        /// <see cref="ConfigureCookies"/>: with private browsing on, an endless feed
        /// accumulates its storage in RAM until the low-memory killer arrives, which
        /// fits "reels close the app, other video is fine" better than planes ever
        /// did. So the default is the engine's own again, and this is a toggle for
        /// the set that needs the other one rather than an opinion about all of them.
        ///
        /// **Not called on a view that has not loaded a page — and that is now a
        /// habit rather than a finding.** It used to be called during start-up,
        /// straight after the WebView was constructed, and a Tizen 10 set that then
        /// never began a load at all made this the suspect: the black screen
        /// appeared right after that reporter's own `5` put a preference on disk,
        /// and it survived an app restart and a TV restart the way a file does. The
        /// deferral shipped in `build-274157b` and the black screen came back
        /// unchanged, with the trail showing a dead launch under the overlay path
        /// and another under the in-page one. So the setting is not what does it.
        /// The deferral stays because it costs nothing and the start-up ordering is
        /// still the one thing about this property nobody has evidence *for*; what
        /// does not stay is treating it as the explanation for a view that will not
        /// navigate. That is <see cref="CheckSomethingLoaded"/>'s problem now.
        /// </summary>
        private void ApplyVideoPath(bool overlay)
        {
            _videoOverlay = overlay;
            _videoPathPending = false;

            try
            {
                // This one bool is the whole video-path API, which is worth knowing
                // before anyone goes looking for a second knob to fix the in-page
                // black screen with. Reflecting over WebView, WebSettings and the
                // Interop layer of Samsung.Tizen.Ref 9.0.104 turns up no
                // render-rectangle setter and no way to hand the view a native
                // window; WebSettings.EnableExtraFeature is a fixed table of browser
                // UI toggles with nothing about video in it. See *There is no second
                // lever* in docs/INTERNALS.md — in-page is a dead path on the 2025
                // sets and `overlay` is the only branch that puts a picture on screen.
                _web.VideoHoleEnabled = overlay;
                Breadcrumbs.Drop("video path: " + (overlay ? "hardware overlay" : "in page"));
            }
            catch (Exception ex)
            {
                // Best-effort like everything else that reaches past the managed
                // surface: an engine without the property plays video the way it
                // always did.
                DiagLog.Add("video path could not be set: " + ex.Message);
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

            NoteMemory();

            // Deliberately here and not in the load-finished callback: the engine
            // is still inside its own notification there, and the one thing this
            // property has already been shown to do is upset a view being set up.
            if (_videoPathPending && _pageEverLoaded)
            {
                ApplyVideoPath(_videoOverlay);
            }

            CheckSomethingLoaded();

            // The menu joins the list now that the pointer no longer keeps the bar
            // alive: arrowing through the menu is pointer keys, and without this the
            // chrome would time out underneath an open menu.
            bool busy = _loading || _keyboard.IsVisible || _overlayVisible ||
                        (_menu != null && _menu.Visible);
            if (_chromeVisible && !busy && DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(4))
            {
                HideChrome();
            }

            return true;
        }

        /// <summary>
        /// Notices a web view that never even starts the load it was given, and
        /// tries the things that could still change the answer.
        ///
        /// Every other failure this app can have leaves a line somewhere: a load
        /// that fails raises PageLoadError, a page that kills the process ends the
        /// trail on its address, a launch that dies never gets here at all. A view
        /// that silently declines to navigate leaves nothing — the app is alive,
        /// the menu opens, the report answers, and the screen is black. Issue #20
        /// spent a round trip on exactly that, and the only reason it was readable
        /// afterwards is that the *absence* of "load started" happened to be
        /// visible in a log that had room for it.
        ///
        /// **The first version of this recovered by putting video back in the page,
        /// and that was worth nothing.** The theory was that a stored overlay
        /// applied to a fresh view is the cause; issue #20's fourth report killed
        /// it twice over. A launch died with the overlay in force and another died
        /// with the in-page path in force, so the setting is not what does it — and
        /// because the recovery applied a fixed value rather than the *other* one,
        /// in every trail we have it re-applied the path that was already in force.
        /// It was a no-op that then declared the view dead. The one thing a rung of
        /// this ladder must do is change something.
        ///
        /// So each rung now does, and each is named on the trail before it is tried,
        /// which makes the report say which one worked:
        ///
        /// 1. **Rebuild the view.** A WebView is a managed object over an engine
        ///    that outlives it; if what is wrong is this view rather than the
        ///    engine's state, a new one is the whole fix and costs nothing.
        /// 2. **Clear the stored session, then rebuild again.** The only thing on
        ///    disk that a dead view could be reading is the profile
        ///    <see cref="ConfigureCookies"/> gave it — the same profile that keeps
        ///    a site logged in, and the only candidate left once the video path is
        ///    out. Clearing it costs the sign-ins, which is why it is the second
        ///    rung and not the first, and why it says so on screen.
        /// 3. **Say the view is not starting loads at all** rather than retry in
        ///    silence, because at that point nothing this app owns is left to try.
        /// </summary>
        private void CheckSomethingLoaded()
        {
            if (_loadAskedAt == DateTime.MinValue ||
                DateTime.UtcNow - _loadAskedAt < TimeSpan.FromSeconds(BlankViewSeconds))
            {
                return;
            }

            _loadAskedAt = DateTime.MinValue;
            _blankRecoveries++;

            Breadcrumbs.Drop("no load began within " + BlankViewSeconds + "s (attempt " +
                             _blankRecoveries + ")");

            // The start screen gets a rung of its own in front of the others, and
            // issue #53 is why. Its set walked the whole ladder — new view, cleared
            // session, new view again — and every rung asked for the *same* page,
            // which was the one thing wrong: a start screen that had swallowed
            // eight copies of itself and outgrown what the engine will load. A
            // typed address loaded first time on the "dead" view. So when what
            // failed is the page this app generates, the first thing to change is
            // the page, and <see cref="ShowHome"/> builds it bare while a recovery
            // is in progress; the report then carries its size either way.
            bool startScreen = _loadAskedFor == null;
            int rung = startScreen ? _blankRecoveries : _blankRecoveries + 1;

            if (rung == 1)
            {
                _blankState = "start screen would not load — shown without its tiles";
                Flash("The start screen would not load — showing it without your tiles");
            }
            else if (rung == 2)
            {
                _blankState = (startScreen ? "bare start screen" : "no load") +
                              " would not load either — rebuilt the view";
                Flash("Nothing loaded — starting the browser engine over");
                if (!RebuildWebView())
                {
                    return;
                }
            }
            else if (rung == 3)
            {
                _blankState = "still nothing after a rebuild — cleared the session and rebuilt";
                Flash("Still nothing — clearing saved sign-ins and trying once more");
                ClearStoredSession();
                if (!RebuildWebView())
                {
                    return;
                }
            }
            else
            {
                // Not "at all": issue #53's set reached this rung and then loaded a
                // typed address at the first ask. What is known here is only that
                // *this* load will not begin, so that is what gets said.
                _blankState = "the web view is not starting this load";
                Breadcrumbs.Drop(_blankState);
                Flash("This will not load. Try typing an address (0). Report at http://<this TV>:8081");
                return;
            }

            RetryLastLoad();
        }

        /// <summary>Asks again for whatever the dead view was given, home included.</summary>
        private void RetryLastLoad()
        {
            if (_loadAskedFor == null)
            {
                ShowHome();
            }
            else
            {
                Navigate(_loadAskedFor);
            }
        }

        /// <summary>
        /// Throws this WebView away and builds another one.
        ///
        /// The engine is a process-wide thing that outlives any one view, so this
        /// is not "restart the browser" — it is the cheapest way to find out
        /// whether a view that will not navigate is broken in itself or is reading
        /// something broken underneath it. Nothing else in the app has to know: the
        /// chrome, the keyboard and the menu are DALi objects that never touched
        /// the old view, and everything the new one needs is re-applied here.
        ///
        /// The video preference deliberately goes back to pending rather than being
        /// carried over — see <see cref="ApplyVideoPath"/>. A rebuilt view has not
        /// loaded a page, and the one rule that survives from the old theory is
        /// that this property is only ever handed to a view that has.
        /// </summary>
        private bool RebuildWebView()
        {
            Breadcrumbs.Drop("rebuilding the web view");

            try
            {
                if (_web != null)
                {
                    _window.Remove(_web);
                    _web.Dispose();
                    _web = null;
                }
            }
            catch (Exception ex)
            {
                // Best-effort like everything else past the managed surface: a view
                // that will not go away quietly is still worth replacing.
                Breadcrumbs.Drop("old view would not close: " + ex.GetType().Name + ": " + ex.Message);
            }

            _pageEverLoaded = false;

            if (!TryStartEngine())
            {
                _blankState = "rebuild failed — " + (_engineFailure ?? "no engine");
                Flash("The browser engine will not start. Press 3.");
                return false;
            }

            ApplyPreset(_presetIndex);
            Breadcrumbs.Drop("web view rebuilt");
            return true;
        }

        /// <summary>
        /// Empties everything the engine keeps between launches.
        ///
        /// This is the reinstall issue #20 was told it might need, without the
        /// reinstall: the sign-ins go, the favourites and history do not, because
        /// those are ours (<see cref="Store"/>) and live in files the engine has
        /// never heard of. Every call is named on the trail *before* it is made,
        /// for the usual reason — if one of them is what takes the process down,
        /// the last line is the answer.
        /// </summary>
        private void ClearStoredSession()
        {
            try
            {
                WebCookieManager cookies = _web == null ? null : _web.CookieManager;
                if (cookies != null)
                {
                    Breadcrumbs.Drop("clearing session: cookies");
                    cookies.ClearCookies();
                }
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("cookies would not clear: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                WebContext context = _web == null ? null : _web.Context;
                if (context == null)
                {
                    Breadcrumbs.Drop("clearing session: engine offered no context");
                    return;
                }

                Breadcrumbs.Drop("clearing session: cache");
                context.ClearCache();

                Breadcrumbs.Drop("clearing session: web storage");
                context.DeleteAllWebStorage();

                Breadcrumbs.Drop("clearing session: web databases");
                context.DeleteAllWebDatabase();

                Breadcrumbs.Drop("clearing session: application cache");
                context.DeleteAllApplicationCache();

                Breadcrumbs.Drop("session cleared");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("session would not clear: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Writes this process's size to the trail every few seconds. Trail-only
        /// and never to the on-screen log, which keeps 60 lines and would lose
        /// everything worth reading inside a minute — see <see cref="Heartbeat"/>
        /// for the same reasoning about a line that repeats.
        ///
        /// The interval is the point: an app being evicted for memory is not a
        /// moment, it is a slope, and a slope only exists if something was writing
        /// numbers down before anybody knew there was a problem.
        /// </summary>
        private void NoteMemory()
        {
            int seconds = NuiMediaWatch.VideoPlaying ? MemorySecondsWithVideo : MemorySecondsIdle;
            if (DateTime.UtcNow - _lastMemoryNote < TimeSpan.FromSeconds(seconds))
            {
                return;
            }

            _lastMemoryNote = DateTime.UtcNow;

            // Piggy-backed on the one thing that already runs on a known interval,
            // and it is a single stat of an open file. See
            // NativeStdErr.StartSession: the capture runs for the whole session so
            // that a crashing render process gets to say something, and a capture
            // that runs for a whole session has to be bounded.
            NativeStdErr.TrimSession(StdErrLimitBytes);

            long mb = ProcessMemory.ResidentMb();
            if (mb < 0)
            {
                return;
            }

            if (mb > _peakMemoryMb)
            {
                _peakMemoryMb = mb;
            }

            Breadcrumbs.DropToTrail("memory: " + mb + " MB resident (peak " + _peakMemoryMb + ")");
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

        /// <summary>
        /// The remote card is the surface that teaches the keys (issue #38): every
        /// key this build answers to, each with the one line that says what it is
        /// for, because it is the only surface visible while a page is open, which
        /// is when a question about a key actually occurs. The start screen says
        /// where this card is and little else; the menu rows stay labels.
        /// </summary>
        private void BuildHints()
        {
            Size2D screen = _window.WindowSize;
            int width = 640;
            int height = 688;

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
                new[] { "hold OK", "menu — every action, on any remote" },
                new[] { "Ch up/down", "scroll a page" },
                new[] { "0", "type an address" },
                new[] { "1", "identify as… — a lighter site, or desktop" },
                new[] { "2", "type in the field you clicked" },
                new[] { "3", "diagnostics — the report at :8081" },
                new[] { "4", "keys to page — when a search box is dead" },
                new[] { "5", "video: TV overlay / in page — if black" },
                new[] { "6", "fit page — when the page is cut off" },
                new[] { "7", "hide this card" },
                new[] { "8", "keep this page as a tile" },
                new[] { "9", "start screen" },
                new[] { "Info", "images off — the one real speed-up" },
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
                new RemoteMenu.Item(RemoteMenu.ActionAdBlock, "Ad blocking on/off", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionVideoPath, "Video: in page / overlay", "5"),
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

                case RemoteMenu.ActionAdBlock:
                    ToggleAdBlock();
                    break;

                case RemoteMenu.ActionVideoPath:
                    ApplyVideoPath(!_videoOverlay);
                    _videoPathChosen = true;
                    Store.Set("videoOverlay", _videoOverlay);
                    Flash(_videoOverlay
                        ? "Video: hardware overlay — the TV's own path"
                        : "Video: in page — try this if video closes the app");

                    // The engine reads this when a video element is created, so the
                    // page that is already open keeps whatever it started with.
                    _web.Reload();
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

            // Only the buttons that earn the bar its four seconds: going back, the
            // menu, a numbered shortcut. The bar used to come up on every key down,
            // each repeat re-arming its own idle timer, so it sat on screen the
            // whole time anyone was moving the cursor (issue #20). Taking the
            // pointer keys out was not enough — the reporter pages through a feed
            // with the channel rocker, and that brought the bar back on every
            // page — so the rule is now the short list of buttons that do show it,
            // and nothing a person holds down to get through a page is on it.
            if (RemoteKeys.IsChromeKey(key))
            {
                ShowChrome();
            }

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

                case RemoteKeys.Num5:
                    RunAction(RemoteMenu.ActionVideoPath);
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
                _loadAskedAt = DateTime.UtcNow;
                _loadAskedFor = null;

                // Bare while the blank-view ladder is climbing — see
                // CheckSomethingLoaded. The tiles are the only part of this page
                // that changes from one launch to the next, so they are the part a
                // recovery has to take away to learn anything.
                bool bare = _blankRecoveries > 0;
                IList<Bookmark> favourites = bare ? new List<Bookmark>() : Store.AllFavourites;
                IList<Bookmark> history = bare ? new List<Bookmark>() : Store.RecentHistory;
                string html = HomePage.Build(favourites, history, Urls.Home);

                // The engine carries this page as a percent-encoded data: URL, so
                // what it is asked to load is a few times this long, and Chromium
                // refuses any URL past 2 MB in silence. The report says how close.
                _startPageState = (html.Length / 1024) + " KB of HTML, " +
                                  Math.Min(favourites.Count, 12) + " favourite + " +
                                  Math.Min(history.Count, 8) + " recent tiles" +
                                  (bare ? "  (bare — recovering)" : string.Empty);

                _web.LoadHtmlString(html);
                _cursor.Center();
                DiagLog.Add("home screen shown" + (bare ? " (bare)" : string.Empty));
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
            _loadAskedAt = DateTime.UtcNow;
            _loadAskedFor = url;
            Breadcrumbs.Drop("navigate: " + url);
            _web.LoadUrl(url);
            _cursor.Center();
            UpdateStatus();
        }

        /// <summary>
        /// The switch is read on the request thread, so flipping it takes effect on
        /// the next request; the reload is so the page that is open shows the
        /// difference rather than the next one. Menu only — every digit and Info
        /// are spoken for, and the menu is what a slim remote uses anyway.
        /// </summary>
        private void ToggleAdBlock()
        {
            _adBlockOn = !_adBlockOn;
            NuiAdBlock.Enabled = _adBlockOn;
            Store.Set("adblock", _adBlockOn);
            DiagLog.Add("ad block " + (_adBlockOn ? "ON" : "OFF"));
            Flash(_adBlockOn ? "Ad blocking on" : "Ad blocking off — ads and trackers load");
            UpdateStatus();
            try
            {
                _web.Reload();
            }
            catch (Exception ex)
            {
                DiagLog.Add("reload after ad block toggle failed: " + ex.Message);
            }
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
                           (_imagesOn ? string.Empty : "   ·   no images") +
                           (_adBlockOn ? string.Empty : "   ·   ads allowed");
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
                  "cookies   : " + _cookieState + "\n" +
                  "video     : " + VideoPathLine() + "\n" +
                  "media     : " + NuiMediaWatch.LastCensus + "\n" +
                  "video cap : " + NuiVideoCap.LastAction + "\n" +
                  "video rect: " + NuiVideoRect.LastBox + "\n" +
                  "blank view: " + _blankState + "\n" +
                  "start page: " + _startPageState + "\n" +
                  "ad block  : " + NuiAdBlock.Summary() + "\n" +
                  "memory    : " + ProcessMemory.Summary() + ", peak " + _peakMemoryMb + " MB\n" +
                  "last words: " + NuiDeathWatch.LastWord + "\n" +
                  "stderr    : " + NativeStdErr.SessionState + "\n" +
                  "url       : " + _cachedUrl;

            // The previous run's trail, exactly as the ElmSharp report carries it.
            // A page that closes the app leaves nothing to read in this run: the
            // launch that died is the one with the answer in it.
            return "Overscan diagnostics (NUI build)\n\n" +
                   "platform  : NUI WebView, api-version 9.0+\n" +
                   "trail file : " + Breadcrumbs.Location + "\n" +
                   engine + "\n\n" +
                   // Issue #37. dest is the engine's own Sec-Fetch-Dest for the
                   // request, and build-f295172's report answered what it was put
                   // here to ask: it is a dash on every line, so that header is not
                   // on the request where Tizen's hook sits, though Sec-Fetch-Mode
                   // and Range both are. The column stays because it costs nothing
                   // and a firmware that starts sending it would be worth knowing
                   // about; the section itself is what found the ad's host.
                   "requests this run (one line per host and first path segment, most first)\n" +
                   RequestTrail.Dump() + "\n\n" +
                   "previous run (last line is where it died)\n" + Breadcrumbs.Previous + "\n\n" +
                   "previous run's native output\n" + Breadcrumbs.PreviousStdErr + "\n\n" +
                   "log\n" + DiagLog.Dump();
        }

        /// <summary>
        /// What the engine has actually been told, which is not the same thing as
        /// what the setting says. A report reading "hardware overlay" while the
        /// property had never been set is a large part of why the black screen on
        /// issue #20 took a round trip to find, so an untouched engine says so.
        /// </summary>
        private string VideoPathLine()
        {
            string path = _videoOverlay ? "hardware overlay" : "in page";

            // Still worth three states rather than two. "Not applied yet" and
            // "applied" are the difference between a report that says what the
            // engine was told and one that says what we intend to tell it, and the
            // gap between them is a whole page load wide.
            if (_videoPathPending)
            {
                return path + "  (key 5 — applies after the first page)";
            }

            return _videoPathChosen
                ? path + "  (key 5)"
                : path + "  (key 5 — our default, nobody chose it)";
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
