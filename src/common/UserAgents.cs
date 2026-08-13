using System.Globalization;
using System.Text.RegularExpressions;

namespace Overscan
{
    internal sealed class UserAgentPreset
    {
        public UserAgentPreset(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }

        /// <summary>The UA string to force, or null to keep the engine default.</summary>
        public string Value { get; }
    }

    /// <summary>
    /// The reason this app exists: the TV's stock UA (…Tizen 5.0…Overscan/x.y…)
    /// makes a lot of sites serve a stripped "smart TV" or legacy-mobile layout.
    /// Overriding it with a desktop UA gets the normal site.
    /// </summary>
    internal static class UserAgents
    {
        private const string WindowsPlatform =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{0} Safari/537.36";

        /// <summary>
        /// Fallback Chromium version if we cannot read one out of the engine's own
        /// UA. Kept deliberately modest — see <see cref="MatchingEngine"/>.
        /// </summary>
        private const string FallbackChromeVersion = "108.0.0.0";

        /// <summary>
        /// A desktop UA whose Chrome version matches the version the TV's engine
        /// actually is. This is the safest default: we get the desktop layout
        /// without claiming JS/CSS support the (older) engine does not have.
        /// Tizen TV engines: 5.0 = M63, 5.5 = M69, 6.0 = M76, 6.5 = M85,
        /// 7.0 = M94, 8.0 = M108, 10.0 = M130 (measured on the emulator — recent
        /// platforms are not the ancient engines the older TVs are).
        /// </summary>
        public static UserAgentPreset MatchingEngine(string engineUserAgent)
        {
            string version = ChromeVersionOf(engineUserAgent) ?? FallbackChromeVersion;
            return new UserAgentPreset(
                "Desktop Chrome " + MajorOf(version) + " (engine-matched)",
                string.Format(WindowsPlatform, version));
        }

        /// <summary>
        /// Presets cycled by the "1" key. Index 0 is replaced at startup with
        /// <see cref="MatchingEngine"/> once the engine's own UA is known.
        /// </summary>
        public static UserAgentPreset[] Defaults()
        {
            return new[]
            {
                MatchingEngine(null),
                new UserAgentPreset("Desktop Chrome 125 (spoofed newer)",
                    string.Format(WindowsPlatform, "125.0.0.0")),
                new UserAgentPreset("Desktop Safari 17 (macOS)",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 " +
                    "(KHTML, like Gecko) Version/17.4 Safari/605.1.15"),
                new UserAgentPreset("TV default (no override)", null),
            };
        }

        public static string ChromeVersionOf(string userAgent)
        {
            if (string.IsNullOrEmpty(userAgent))
            {
                return null;
            }

            // Desktop/mobile Chromium form, and older Samsung TVs, carry a
            // Chrome/<version> token.
            Match m = Regex.Match(userAgent, @"Chrome/(\d+(?:\.\d+)*)");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }

            // Newer Samsung TVs drop the token and put the engine version bare,
            // right after the Gecko comment — verified on Tizen 10.0:
            //   Mozilla/5.0 (SMART-TV; LINUX; Tizen 10.0) AppleWebKit/537.36
            //   (KHTML, like Gecko) 130.0.6723.116/10.0 TV Safari/537.36
            // Missing this is not harmless: it silently fell back to claiming 108
            // on an engine that is actually M130.
            m = Regex.Match(userAgent, @"like Gecko\)\s+(\d+(?:\.\d+){2,})");
            if (m.Success)
            {
                return m.Groups[1].Value;
            }

            // Older sets state no engine version at all — verified on a 2019
            // RU7020 (Tizen 5.0):
            //   … AppleWebKit/537.36 (KHTML, like Gecko) Version/5.0 TV Safari/537.36
            // Guessing high here is actively harmful: claiming Chrome 108 to a site
            // while running M63 invites JavaScript the engine cannot parse. Derive
            // the milestone from the platform version instead.
            m = Regex.Match(userAgent, @"Tizen (\d+\.\d+)");
            return m.Success ? MilestoneForPlatform(m.Groups[1].Value) : null;
        }

        /// <summary>
        /// Tizen TV platform version to Chromium milestone. 10.0 is measured; the
        /// rest are Samsung's published web-engine specifications. An unknown
        /// version takes the highest milestone at or below it, so a newer platform
        /// errs on the conservative side rather than over-claiming.
        /// </summary>
        private static string MilestoneForPlatform(string platformVersion)
        {
            var table = new[]
            {
                new[] { "3.0", "47" }, new[] { "4.0", "56" }, new[] { "5.0", "63" },
                new[] { "5.5", "69" }, new[] { "6.0", "76" }, new[] { "6.5", "85" },
                new[] { "7.0", "94" }, new[] { "8.0", "108" }, new[] { "10.0", "130" },
            };

            double wanted;
            if (!double.TryParse(platformVersion, NumberStyles.Float, CultureInfo.InvariantCulture, out wanted))
            {
                return null;
            }

            string best = null;
            foreach (string[] row in table)
            {
                double version = double.Parse(row[0], CultureInfo.InvariantCulture);
                if (version <= wanted)
                {
                    best = row[1];
                }
            }

            return best == null ? null : best + ".0.0.0";
        }

        private static string MajorOf(string version)
        {
            int dot = version.IndexOf('.');
            return dot < 0 ? version : version.Substring(0, dot);
        }
    }
}
