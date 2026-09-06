using System;
using System.Collections.Generic;
using System.IO;

namespace Overscan
{
    /// <summary>A saved page.</summary>
    internal sealed class Bookmark
    {
        public Bookmark(string url, string title)
        {
            Url = url;
            Title = title;
        }

        public string Url { get; private set; }

        public string Title { get; private set; }
    }

    /// <summary>
    /// Favourites, history and settings, in three plain text files.
    ///
    /// Tab-separated lines rather than JSON on purpose: the tizen50 build targets
    /// .NET Core 2.x, where `System.Text.Json` is not in the framework, and pulling
    /// a NuGet package in to store a handful of URLs would be silly. Every write is
    /// a full rewrite — these files are tiny and a TV browser has no concurrency.
    ///
    /// Failures are swallowed and logged: losing a bookmark must never stop the
    /// browser from starting.
    /// </summary>
    internal static class Store
    {
        private const int HistoryLimit = 120;

        private static string _dir;
        private static readonly List<Bookmark> Favourites = new List<Bookmark>();
        private static readonly List<Bookmark> History = new List<Bookmark>();
        private static readonly Dictionary<string, string> Settings = new Dictionary<string, string>();

        public static void Init(string dataDirectory)
        {
            _dir = dataDirectory;
            Load(Path.Combine(_dir, "favourites.tsv"), Favourites);
            Load(Path.Combine(_dir, "history.tsv"), History);
            LoadSettings();

            // Heal what an earlier build wrote. Issue #53's set had eight copies of
            // its own start screen in history, each one containing the ones before
            // it — see IsGenerated for how that happened and what it did. Dropping
            // them here is what turns that set's black screen back into a browser
            // without asking anybody to reinstall.
            int dropped = DropGenerated(Favourites, "favourites.tsv") +
                          DropGenerated(History, "history.tsv");
            if (dropped > 0)
            {
                DiagLog.Add("store: dropped " + dropped + " generated page(s) that had been saved as visits");
            }

            // Same idea, one level down: the sign-in waypoints an earlier build
            // recorded as visits. History only — a favourite is something a person
            // pressed a key for, and stays whatever it points at.
            int passed = DropWaypoints(History, "history.tsv");
            if (passed > 0)
            {
                DiagLog.Add("store: dropped " + passed + " sign-in page(s) from history");
            }

            // And the same page kept twice under two spellings of its address —
            // issue #80. See SameKey for how somebody ends up with two.
            int twice = DropDuplicates(Favourites, "favourites.tsv") +
                        DropDuplicates(History, "history.tsv");
            if (twice > 0)
            {
                DiagLog.Add("store: dropped " + twice + " duplicate page(s)");
            }

            DiagLog.Add("store: " + Favourites.Count + " favourites, " + History.Count +
                        " history, " + Settings.Count + " settings");
        }

        /// <summary>
        /// Whether a URL is one of this app's own generated pages rather than
        /// somewhere the user went. Such a page must never be saved as a visit or
        /// a favourite: it is rebuilt on demand, and a saved copy is a stale one.
        ///
        /// It comes in two shapes. The ElmSharp build loads the start screen with
        /// <see cref="HomePage.BaseUrl"/> as its base, so that is the URL the
        /// engine reports for it. The NUI WebView has no base-URL overload, and
        /// what it reports for a page loaded from a string is a <c>data:</c> URL
        /// carrying the whole page, percent-encoded. Issue #53 is what one check
        /// without the other does: every start screen was recorded as a visit,
        /// so the next start screen carried the previous one inside a tile, and
        /// the one after that carried both. The page roughly doubled with every
        /// launch — 3.5 KB, 12 KB, ..., 1.5 MB — until it passed Chromium's
        /// 2 MB ceiling on a URL, after which the engine dropped the load without
        /// a word: no start, no error, a black screen on every launch that
        /// survived a reinstall of nothing but the engine's own profile, because
        /// history is ours and lives in a file the engine has never heard of.
        ///
        /// No page a user could visit is a <c>data:</c> URL worth keeping either,
        /// so the scheme check is safe for every build.
        /// </summary>
        public static bool IsGenerated(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            return url.StartsWith(HomePage.BaseUrl, StringComparison.Ordinal) ||
                   url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Longest URL worth keeping as a visit. Nobody typed a longer one, no tile
        /// can show it, and every one of them is a token some site put in the
        /// address for its own use — issue #53's report has recaptcha pages at
        /// 3 KB apiece sitting in the reporter's recent tiles. The start screen
        /// carries every recent URL inside itself, and #53 is what happens when
        /// that page is allowed to grow without anyone watching, so the ceiling
        /// is part of keeping it small as well as tidy.
        /// </summary>
        private const int VisitUrlLimit = 1024;

        /// <summary>
        /// Path segments that mark a page a sign-in flow passes through on the way
        /// to somewhere else: the captcha, the code entry, the consent screen, the
        /// "continue as" page. Matched as whole segments of the path, lower-case,
        /// never against the host or the query — <c>login.example.com/</c> is a
        /// site, <c>/daily-challenge/</c> is a page somebody may want back, and
        /// neither should be touched. These are the shapes seen in real trails
        /// (Instagram's <c>auth_platform/recaptcha/</c>, <c>auth_platform/codeentry/</c>
        /// and <c>accounts/onetap/</c>) plus the names OAuth and the big identity
        /// providers use for the same steps.
        /// </summary>
        private static readonly string[] WaypointSegments =
        {
            "auth_platform", "recaptcha", "captcha", "challenge", "onetap", "codeentry",
            "oauth", "oauth2", "authorize", "consent", "sso", "signin", "sign-in", "login",
        };

        /// <summary>
        /// Whether a URL is a page a user passes through rather than one they went
        /// to: a step of a sign-in flow, or an address too long to be anything but a
        /// token. Not recorded as a visit, because revisiting it either fails (the
        /// token is spent) or bounces straight to the page it was in front of, and
        /// on the start screen it shows as yet another tile named after the site's
        /// host with nothing to tell it apart from the real one. Favourites are not
        /// filtered by this — a favourite is an explicit act.
        /// </summary>
        public static bool IsWaypoint(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            if (url.Length > VisitUrlLimit)
            {
                return true;
            }

            // Path only: after the host, before the query or fragment.
            int start = url.IndexOf("://", StringComparison.Ordinal);
            start = start < 0 ? 0 : url.IndexOf('/', start + 3);
            if (start < 0)
            {
                return false;
            }

            int end = url.Length;
            int query = url.IndexOf('?', start);
            if (query >= 0)
            {
                end = query;
            }

            int fragment = url.IndexOf('#', start);
            if (fragment >= 0 && fragment < end)
            {
                end = fragment;
            }

            string[] segments = url.Substring(start, end - start).ToLowerInvariant().Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                for (int j = 0; j < WaypointSegments.Length; j++)
                {
                    if (segments[i] == WaypointSegments[j])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static IList<Bookmark> AllFavourites
        {
            get { return Favourites; }
        }

        /// <summary>Most recent first.</summary>
        public static IList<Bookmark> RecentHistory
        {
            get { return History; }
        }

        public static bool IsFavourite(string url)
        {
            return IndexOf(Favourites, url) >= 0;
        }

        /// <summary>Adds or removes; returns true when the page is now a favourite.</summary>
        public static bool ToggleFavourite(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url == "-" || IsGenerated(url))
            {
                return false;
            }

            int at = IndexOf(Favourites, url);
            if (at >= 0)
            {
                Favourites.RemoveAt(at);
                Save("favourites.tsv", Favourites);
                return false;
            }

            Favourites.Insert(0, new Bookmark(url, string.IsNullOrEmpty(title) ? url : title));
            Save("favourites.tsv", Favourites);
            return true;
        }

        /// <summary>
        /// Keeps a page, and says whether it was already kept. Never removes.
        ///
        /// The add-only half of <see cref="ToggleFavourite"/>, for "Keep an
        /// address…" — a menu row with the word *keep* in it must not sometimes
        /// delete, and the reporter on issue #80 spent an afternoon in the gap
        /// between those two readings.
        /// </summary>
        public static bool Keep(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url == "-" || IsGenerated(url) || IndexOf(Favourites, url) >= 0)
            {
                return false;
            }

            Favourites.Insert(0, new Bookmark(url, string.IsNullOrEmpty(title) ? url : title));
            Save("favourites.tsv", Favourites);
            return true;
        }

        /// <summary>Drops a favourite. True when there was one to drop.</summary>
        public static bool RemoveFavourite(string url)
        {
            int at = IndexOf(Favourites, url);
            if (at < 0)
            {
                return false;
            }

            Favourites.RemoveAt(at);
            Save("favourites.tsv", Favourites);
            return true;
        }

        /// <summary>Drops a visit. True when there was one to drop.</summary>
        public static bool ForgetVisit(string url)
        {
            int at = IndexOf(History, url);
            if (at < 0)
            {
                return false;
            }

            History.RemoveAt(at);
            Save("history.tsv", History);
            return true;
        }

        public static void RecordVisit(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url == "-" || url.StartsWith("about:", StringComparison.Ordinal))
            {
                return;
            }

            // The home page is generated, not visited — in either of its shapes.
            if (IsGenerated(url))
            {
                return;
            }

            // A sign-in step is passed through, not visited.
            if (IsWaypoint(url))
            {
                return;
            }

            int at = IndexOf(History, url);
            if (at >= 0)
            {
                History.RemoveAt(at);
            }

            History.Insert(0, new Bookmark(url, string.IsNullOrEmpty(title) ? url : title));
            while (History.Count > HistoryLimit)
            {
                History.RemoveAt(History.Count - 1);
            }

            Save("history.tsv", History);
        }

        public static string Get(string key, string fallback)
        {
            string value;
            return Settings.TryGetValue(key, out value) ? value : fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            int value;
            return int.TryParse(Get(key, null), out value) ? value : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            string value = Get(key, null);
            return value == null ? fallback : value == "1";
        }

        public static void Set(string key, string value)
        {
            Settings[key] = value ?? string.Empty;
            SaveSettings();
        }

        public static void Set(string key, int value)
        {
            Set(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        public static void Set(string key, bool value)
        {
            Set(key, value ? "1" : "0");
        }

        /// <summary>
        /// The two spellings of one page, reduced to one.
        ///
        /// Favourites were matched by exact string, and issue #80's reporter found
        /// what that costs the moment there are two ways to write the same address:
        /// he kept <c>instagram.com/reel</c> by typing it, and Instagram's own URL
        /// for that page is <c>instagram.com/reel/</c>. So pressing 8 there did not
        /// find his favourite — it added a second one — and both tiles are named
        /// after the same site, so what he saw was a page he had just been told was
        /// removed, still sitting in his favourites.
        ///
        /// The trailing slash on the path is folded, and a leading <c>www.</c> on
        /// the host — the two parts of an address a person neither sees nor types.
        /// The start screen shows a tile's host with the <c>www.</c> already
        /// stripped, so without that half, typing back exactly what is on the
        /// screen still failed to find the favourite it names.
        ///
        /// A query and a fragment stay significant, because two addresses that
        /// differ there are two pages as often as they are one, and a favourite is
        /// an explicit act that nobody should have quietly widened for them.
        /// </summary>
        private static string SameKey(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            int cut = url.Length;
            int query = url.IndexOf('?');
            if (query >= 0)
            {
                cut = query;
            }

            int fragment = url.IndexOf('#');
            if (fragment >= 0 && fragment < cut)
            {
                cut = fragment;
            }

            string head = url.Substring(0, cut);

            // Never into the "//" of the scheme: "https://" must not become
            // "https:/", which would fold every site on earth onto one key.
            int authority = head.IndexOf("://", StringComparison.Ordinal);
            int floor = authority < 0 ? 0 : authority + 3;
            while (head.Length > floor && head[head.Length - 1] == '/')
            {
                head = head.Substring(0, head.Length - 1);
            }

            // And the www. the tile does not show. Only at the start of the host,
            // never anywhere else in the address.
            if (authority >= 0 &&
                head.Length > floor + 4 &&
                string.Compare(head, floor, "www.", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                head = head.Substring(0, floor) + head.Substring(floor + 4);
            }

            return head + url.Substring(cut);
        }

        private static int IndexOf(List<Bookmark> list, string url)
        {
            string key = SameKey(url);
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(SameKey(list[i].Url), key, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Removes entries that are the same page written two ways, keeping the
        /// first — which in both files is the more recent. Heals a file an earlier
        /// build wrote, the same way the generated pages and the sign-in waypoints
        /// are healed: issue #80's reporter has look-alike favourites on his set
        /// now, and a fix that only stops new ones appearing would leave him
        /// deleting the old ones by hand from a page that cannot tell them apart.
        /// </summary>
        private static int DropDuplicates(List<Bookmark> list, string fileName)
        {
            var seen = new List<string>();
            int dropped = 0;
            for (int i = 0; i < list.Count; i++)
            {
                string key = SameKey(list[i].Url).ToLowerInvariant();
                if (seen.Contains(key))
                {
                    list.RemoveAt(i);
                    i--;
                    dropped++;
                }
                else
                {
                    seen.Add(key);
                }
            }

            if (dropped > 0)
            {
                Save(fileName, list);
            }

            return dropped;
        }

        /// <summary>Removes generated pages a previous build let in, and saves if any were.</summary>
        private static int DropGenerated(List<Bookmark> list, string fileName)
        {
            int dropped = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (IsGenerated(list[i].Url))
                {
                    list.RemoveAt(i);
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                Save(fileName, list);
            }

            return dropped;
        }

        /// <summary>Removes sign-in waypoints an earlier build recorded, and saves if any were.</summary>
        private static int DropWaypoints(List<Bookmark> list, string fileName)
        {
            int dropped = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (IsWaypoint(list[i].Url))
                {
                    list.RemoveAt(i);
                    dropped++;
                }
            }

            if (dropped > 0)
            {
                Save(fileName, list);
            }

            return dropped;
        }

        private static void Load(string path, List<Bookmark> into)
        {
            into.Clear();
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(path))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 1 && parts[0].Length > 0)
                    {
                        into.Add(new Bookmark(parts[0], parts.Length > 1 ? parts[1] : parts[0]));
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("store: cannot read " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        private static void Save(string fileName, List<Bookmark> list)
        {
            if (_dir == null)
            {
                return;
            }

            try
            {
                var lines = new List<string>();
                foreach (Bookmark item in list)
                {
                    lines.Add(item.Url + "\t" + (item.Title ?? string.Empty).Replace('\t', ' '));
                }

                File.WriteAllLines(Path.Combine(_dir, fileName), lines.ToArray());
            }
            catch (Exception ex)
            {
                DiagLog.Add("store: cannot write " + fileName + ": " + ex.Message);
            }
        }

        private static void LoadSettings()
        {
            Settings.Clear();
            try
            {
                string path = Path.Combine(_dir, "settings.tsv");
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(path))
                {
                    int split = line.IndexOf('\t');
                    if (split > 0)
                    {
                        Settings[line.Substring(0, split)] = line.Substring(split + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("store: cannot read settings: " + ex.Message);
            }
        }

        private static void SaveSettings()
        {
            if (_dir == null)
            {
                return;
            }

            try
            {
                var lines = new List<string>();
                foreach (KeyValuePair<string, string> pair in Settings)
                {
                    lines.Add(pair.Key + "\t" + pair.Value);
                }

                File.WriteAllLines(Path.Combine(_dir, "settings.tsv"), lines.ToArray());
            }
            catch (Exception ex)
            {
                DiagLog.Add("store: cannot write settings: " + ex.Message);
            }
        }
    }
}
