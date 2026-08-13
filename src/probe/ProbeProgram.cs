using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ElmSharp;
using Tizen.Applications;
using Tizen.WebView;

namespace Overscan
{
    /// <summary>
    /// Bisect harness for a device that cannot be logged: it performs the browser's
    /// startup one call at a time, dropping a breadcrumb before and after each, so a
    /// hard crash identifies its own cause on the next launch.
    ///
    /// Separate package id from the browser so both can be installed at once.
    /// </summary>
    internal static class ProbeProgram
    {
        private const string PackageId = "org.apps2samsung.overscanprobe";

        private static Window _window;
        private static WebView _web;
        private static bool _stopped;
        private static string _failure;
        private static readonly List<string> _found = new List<string>();
        private static string _preloaded;

        private static void Main(string[] args)
        {
            DiagServer.Start();
            DiagServer.ReportProvider = Report;

            Breadcrumbs.Init(PackageId);
            Breadcrumbs.Drop("probe: Main entered");
            Breadcrumbs.Drop("probe: trail at " + Breadcrumbs.Location);

            // Stage 1 runs before Run(), because ElmSharp requires it (TizenFX's own
            // Tizen.WebView sample does the same) — and it is a genuine suspect on a
            // retail TV, so it gets its own breadcrumb pair.
            Step("1 Elementary.Initialize", () =>
            {
                Elementary.Initialize();
                Elementary.ThemeOverlay();
            });

            Breadcrumbs.Drop("probe: calling CoreUIApplication.Run");

            try
            {
                new ProbeApp().Run(args);
                Breadcrumbs.Drop("probe: app loop returned");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("probe: FATAL in Run: " + ex.GetType().Name + ": " + ex.Message);
            }

            // Hold the process so the report stays readable over :8081.
            Breadcrumbs.Drop("probe: holding open for diagnostics");
            Thread.Sleep(Timeout.Infinite);
        }

        /// <summary>
        /// Runs one startup step. A managed exception is recorded and stops the
        /// ladder; a native crash leaves the "start" breadcrumb as the last line on
        /// disk, which is the same information by other means.
        /// </summary>
        internal static void Step(string name, Action action)
        {
            if (_stopped)
            {
                Breadcrumbs.Drop("SKIPPED " + name + " (earlier stage failed)");
                return;
            }

            Breadcrumbs.Drop("STAGE START  " + name);
            try
            {
                action();
                Breadcrumbs.Drop("STAGE OK     " + name);
            }
            catch (Exception ex)
            {
                _failure = name + " -> " + ex.GetType().Name + ": " + ex.Message;
                _stopped = true;
                Breadcrumbs.Drop("STAGE FAILED " + _failure);
                Breadcrumbs.Drop("stack: " + ex.StackTrace);
            }

            // A beat between stages, so a crash cannot outrun the file write.
            Thread.Sleep(300);
        }

        internal static void RunUiStages()
        {
            Step("2 Window create + show", () =>
            {
                _window = new Window("probe");
                _window.Show();
                _window.Active();
            });

            Step("3 Background + Conformant + Box + Label", () =>
            {
                var background = new Background(_window)
                {
                    AlignmentX = -1, AlignmentY = -1, WeightX = 1, WeightY = 1,
                    Color = Color.FromRgb(20, 20, 24),
                };
                background.Show();
                _window.AddResizeObject(background);

                var conformant = new Conformant(_window);
                conformant.Show();

                var box = new Box(_window)
                {
                    AlignmentX = -1, AlignmentY = -1, WeightX = 1, WeightY = 1,
                };
                box.Show();
                conformant.SetContent(box);

                var label = new Label(_window)
                {
                    AlignmentX = -1, AlignmentY = -1, WeightX = 1, WeightY = 1,
                    Text = "<color=#ffffff>Overscan probe: ElmSharp UI is alive</color>",
                };
                label.Show();
                box.PackEnd(label);
            });

            // Stage 4 failed on the RU7020 with DllNotFoundException for
            // libchromium-ewk.so ("liblibchromium-ewk.so.so: cannot open shared
            // object file"), i.e. the loader exhausted its name variants. That has
            // two very different causes — the engine is absent from this firmware,
            // or it is present somewhere .NET does not probe — so look before
            // calling it again.
            Step("4a scan filesystem for the web engine", ScanForEngine);
            Step("4b dlopen the engine directly", PreloadEngine);
            Step("4c open the blocking dependency", ProbeDependency);

            Step("4 Chromium.Initialize", () =>
            {
                int refCount = Chromium.Initialize();
                Breadcrumbs.Drop("             chromium refcount=" + refCount);
            });

            Step("5 new WebView(window)", () =>
            {
                _web = new WebView(_window)
                {
                    AlignmentX = -1, AlignmentY = -1, WeightX = 1, WeightY = 1,
                };
                _web.Show();
            });

            Step("6 read + set UserAgent", () =>
            {
                Breadcrumbs.Drop("             engine UA: " + (_web.UserAgent ?? "(null)"));
                _web.UserAgent = UserAgents.MatchingEngine(_web.UserAgent).Value;
            });

            Step("7 LoadUrl", () =>
            {
                _web.LoadStarted += (s, e) => Breadcrumbs.Drop("             load started");
                _web.LoadFinished += (s, e) => Breadcrumbs.Drop("             load finished: " + _web.Url);
                _web.LoadError += (s, e) => Breadcrumbs.Drop("             load error: " + e.Description);
                _web.LoadUrl("https://example.com/");
            });

            Breadcrumbs.Drop(_stopped ? "LADDER STOPPED: " + _failure : "ALL STAGES PASSED");
        }

        private const int RtldNow = 2;
        private const int RtldGlobal = 0x100;

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlopen(string file, int mode);

        [DllImport("libdl.so.2")]
        private static extern IntPtr dlerror();

        private static readonly string[] SearchDirs =
        {
            "/usr/lib", "/usr/lib64", "/lib", "/usr/lib/chromium-efl", "/usr/lib/ewk",
            "/opt/usr/lib", "/usr/apps/org.tizen.browser/lib",
        };

        private static readonly string[] Keywords =
        {
            "chromium", "ewk", "ewebkit", "webengine", "webview", "blink", "marlin", "drm",
        };

        /// <summary>
        /// Lists engine-looking shared objects. Reading /usr/lib may itself be
        /// denied by Smack, which is why every directory is reported individually.
        /// </summary>
        private static void ScanForEngine()
        {
            foreach (string dir in SearchDirs)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        Breadcrumbs.Drop("             " + dir + " : does not exist");
                        continue;
                    }

                    string[] all = Directory.GetFiles(dir);
                    var hits = new List<string>();
                    foreach (string file in all)
                    {
                        string name = Path.GetFileName(file).ToLowerInvariant();
                        foreach (string keyword in Keywords)
                        {
                            if (name.IndexOf(keyword, StringComparison.Ordinal) >= 0)
                            {
                                hits.Add(Path.GetFileName(file));
                                break;
                            }
                        }
                    }

                    Breadcrumbs.Drop("             " + dir + " : " + all.Length + " files, " +
                                     hits.Count + " engine-like");
                    for (int i = 0; i < hits.Count && i < 15; i++)
                    {
                        Breadcrumbs.Drop("                -> " + hits[i]);
                        _found.Add(Path.Combine(dir, hits[i]));
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Drop("             " + dir + " : " + ex.GetType().Name + " " + ex.Message);
                }
            }
        }

        /// <summary>
        /// dlopen by absolute path, bypassing .NET's probing entirely. If one
        /// succeeds with RTLD_GLOBAL, the later P/Invoke by soname can bind to the
        /// already-loaded library — the same trick used for the Tailscale spike.
        /// </summary>
        private static void PreloadEngine()
        {
            var candidates = new List<string>
            {
                "/usr/lib/libchromium-ewk.so",
                "/usr/lib/libchromium-ewk.so.0",
                "/usr/lib/chromium-efl/libchromium-ewk.so",
                "/usr/lib/libewebkit2.so",
                "/usr/lib/libewebkit2.so.0",
                "libchromium-ewk.so",
                "libchromium-ewk.so.0",
            };
            candidates.AddRange(_found);

            foreach (string candidate in candidates)
            {
                IntPtr handle;
                try
                {
                    dlerror();
                    handle = dlopen(candidate, RtldNow | RtldGlobal);
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Drop("             dlopen(" + candidate + ") threw " + ex.GetType().Name);
                    continue;
                }

                if (handle != IntPtr.Zero)
                {
                    _preloaded = candidate;
                    Breadcrumbs.Drop("             dlopen OK: " + candidate);
                    return;
                }

                string error = Marshal.PtrToStringAnsi(dlerror()) ?? "(no dlerror)";
                Breadcrumbs.Drop("             dlopen FAILED " + candidate + " : " + error);
            }

            Breadcrumbs.Drop("             no engine library could be opened");
        }

        /// <summary>
        /// The engine itself parses fine; dlopen fails resolving libmarlin.so.0
        /// with EPERM. Opening that file directly separates "the privilege now
        /// grants access" from "still denied" in one unambiguous line.
        /// </summary>
        private static void ProbeDependency()
        {
            string[] targets =
            {
                "/usr/lib/libmarlin.so.0",
                "/usr/lib/libmarlin.so",
                "/usr/lib/libchromium-ewk.so",
            };

            foreach (string target in targets)
            {
                try
                {
                    if (!File.Exists(target))
                    {
                        Breadcrumbs.Drop("             " + target + " : File.Exists=false");
                        continue;
                    }

                    using (FileStream stream = File.OpenRead(target))
                    {
                        var head = new byte[4];
                        int read = stream.Read(head, 0, head.Length);
                        Breadcrumbs.Drop("             " + target + " : READABLE (" + read +
                                         " bytes, len=" + stream.Length + ")");
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Drop("             " + target + " : " + ex.GetType().Name +
                                     " - " + ex.Message);
                }
            }
        }

        private static string Report()
        {
            return "Overscan PROBE (tizen50 / Tizen.WebView)\n\n" +
                   "trail file : " + Breadcrumbs.Location + "\n" +
                   "verdict    : " + (_failure ?? (_stopped ? "stopped" : "no managed failure so far")) + "\n" +
                   "engine libs: " + (_found.Count == 0 ? "none found on disk" : string.Join(", ", _found)) + "\n" +
                   "preloaded  : " + (_preloaded ?? "(none)") + "\n" +
                   "\n=== PREVIOUS RUN (the last line is where it died) ===\n" +
                   Breadcrumbs.Previous +
                   "\n=== THIS RUN ===\n" + DiagLog.Dump();
        }
    }

    internal sealed class ProbeApp : CoreUIApplication
    {
        protected override void OnCreate()
        {
            base.OnCreate();
            Breadcrumbs.Drop("probe: OnCreate reached");
            ProbeProgram.RunUiStages();
        }
    }
}
