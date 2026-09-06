using System;
using System.Collections.Generic;

namespace Overscan
{
    /// <summary>
    /// What the browser opens when it launches.
    ///
    /// Issue #15 gave this one answer — a fixed address, set from the keyboard's
    /// <c>start</c> key — because opening on the generated start screen means
    /// typing the same address on a remote control every evening. Issue #79 asked
    /// for the other one: "set start page as the last page we left off". Both are
    /// the same setting with three states, so they live here rather than as two
    /// flags in two apps that could disagree about which of them wins.
    ///
    /// The mode is stored beside the address rather than encoded into it. A
    /// sentinel URL meaning "not a URL" is the kind of thing that reads fine on
    /// the day it is written and turns into a site nobody can visit the first time
    /// somebody's history contains it.
    /// </summary>
    internal static class StartPage
    {
        /// <summary>The generated start screen — favourites and recent tiles.</summary>
        public const string Home = "home";

        /// <summary>Wherever the last session got to.</summary>
        public const string Last = "last";

        /// <summary>One fixed address, set from the keyboard's <c>start</c> key.</summary>
        public const string Url = "url";

        private const string ModeKey = "startupMode";
        private const string UrlKey = "startupUrl";

        /// <summary>
        /// The mode in force. An install from before #79 has no mode key at all, so
        /// it is derived from whether an address was ever set — which is exactly
        /// what those builds did, and means nobody's start page changes under them
        /// on upgrade.
        /// </summary>
        public static string Mode()
        {
            string stored = Store.Get(ModeKey, null);
            if (stored == Home || stored == Last || stored == Url)
            {
                return stored;
            }

            return string.IsNullOrEmpty(Address) ? Home : Url;
        }

        /// <summary>The fixed address, or empty when none was set.</summary>
        public static string Address
        {
            get { return Store.Get(UrlKey, string.Empty) ?? string.Empty; }
        }

        /// <summary>
        /// Remembers a fixed address, and switches to it. An empty address goes
        /// back to the start screen, which is what pressing <c>start</c> with
        /// nothing typed has always done.
        /// </summary>
        public static void SetAddress(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                Store.Set(UrlKey, string.Empty);
                Store.Set(ModeKey, Home);
                return;
            }

            Store.Set(UrlKey, url);
            Store.Set(ModeKey, Url);
        }

        /// <summary>
        /// Turns "open where I left off" on and off. Off goes back to the fixed
        /// address if one was ever set and to the start screen otherwise — so the
        /// address somebody chose is still there when they change their mind, and
        /// nobody has to type it again to get it back.
        /// </summary>
        public static bool ToggleLast()
        {
            bool on = Mode() != Last;
            Store.Set(ModeKey, on ? Last : (string.IsNullOrEmpty(Address) ? Home : Url));
            return on;
        }

        /// <summary>
        /// The address to open at launch, or null for the start screen.
        ///
        /// The history is most-recent-first and already refuses this app's own
        /// generated pages and the sign-in steps a flow passes through
        /// (<see cref="Store.RecordVisit"/>), so its first entry is the last page
        /// the user was actually on — not the start screen they closed the app
        /// from, and not the captcha they went through an hour earlier.
        /// </summary>
        public static string Resolve(IList<Bookmark> history)
        {
            switch (Mode())
            {
                case Url:
                    string url = Address;
                    return string.IsNullOrEmpty(url) ? null : url;

                case Last:
                    // No history is a fresh install, or one whose history was
                    // cleared. The start screen is the honest answer, and it is
                    // the screen that lists what there is.
                    return history != null && history.Count > 0 ? history[0].Url : null;

                default:
                    return null;
            }
        }

        /// <summary>What the diagnostics report says about all this.</summary>
        public static string Describe(IList<Bookmark> history)
        {
            switch (Mode())
            {
                case Url:
                    return "fixed address — " + Address;

                case Last:
                    string last = Resolve(history);
                    return "where you left off — " +
                           (last ?? "(nothing visited yet, so the start screen)");

                default:
                    return "the start screen";
            }
        }
    }
}
