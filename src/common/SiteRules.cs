using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Overscan
{
    /// <summary>
    /// What one site has been asked to be remembered as: the answers this browser
    /// gives that site rather than the ones it gives everywhere.
    /// </summary>
    /// <remarks>
    /// Every field can be <see cref="Unset"/>, and that is not the same as "off".
    /// A rule that says nothing about images leaves the browser-wide setting alone,
    /// so a site remembered only for how it is identified still follows the images
    /// switch when it is flipped later.
    /// </remarks>
    internal sealed class SiteRule
    {
        public const int Unset = -1;

        public SiteRule(string site)
        {
            Site = site;
            Ua = Unset;
            Images = Unset;
            Proxy = Unset;
        }

        /// <summary>
        /// The host this rule answers for, lower-cased and without a leading
        /// <c>www.</c> — see <see cref="SiteRules.KeyFor"/>.
        /// </summary>
        public string Site { get; private set; }

        /// <summary>Index into the UA presets, or <see cref="Unset"/>.</summary>
        public int Ua { get; set; }

        /// <summary>1 for on, 0 for off, or <see cref="Unset"/>.</summary>
        public int Images { get; set; }

        /// <summary>
        /// 1 to go through the proxy, 0 to go direct, or <see cref="Unset"/>.
        /// Issue #97; only the NUI build acts on it, the others carry it through
        /// the file untouched.
        /// </summary>
        public int Proxy { get; set; }

        public bool IsEmpty
        {
            get { return Ua == Unset && Images == Unset && Proxy == Unset; }
        }
    }

    /// <summary>
    /// Per-site settings: which identity a site is given and whether it gets
    /// images, remembered for that site alone.
    ///
    /// Issue #74. The two switches this browser has that change what a site sends
    /// back are exactly the two that want a different answer per site: Instagram's
    /// mobile layout is unusable on a TV so it wants the desktop identity, while
    /// Spotify will not play at all without images, and the reporter was flipping
    /// both by hand on every switch between them. A setting that has to be redone
    /// every time is one the user is maintaining for us.
    ///
    /// Issue #97 added a third: whether the site goes through a proxy. A proxy is
    /// wanted for the one site a region blocks and is a cost (a slower, remote
    /// hop) everywhere else, which is the same shape as the other two.
    ///
    /// Stored in <c>sites.tsv</c>, one line per site, in the same shape as
    /// everything else <see cref="Store"/> keeps and for the same reason: the
    /// tizen50 build has no JSON in its framework.
    ///
    ///     open.spotify.com &lt;TAB&gt; images=1
    ///     instagram.com    &lt;TAB&gt; ua=1 &lt;TAB&gt; images=0
    ///     bbc.co.uk        &lt;TAB&gt; proxy=1
    ///
    /// Failures are swallowed and logged. A browser that will not start because a
    /// preference file is unreadable is a worse browser than one that forgets a
    /// preference.
    /// </summary>
    internal static class SiteRules
    {
        /// <summary>
        /// A ceiling on the file, not on the feature. Rules are only ever written
        /// by a person pressing a key, so this is unreachable in ordinary use; it
        /// is here so that a fault which somehow writes one per page cannot grow a
        /// file the next launch has to read.
        /// </summary>
        private const int RuleLimit = 200;

        private const string FileName = "sites.tsv";

        private static string _dir;
        private static readonly List<SiteRule> Rules = new List<SiteRule>();

        public static void Init(string dataDirectory)
        {
            _dir = dataDirectory;
            Load();
            DiagLog.Add("site rules: " + Rules.Count + " site(s) remembered");
        }

        public static IList<SiteRule> All
        {
            get { return Rules; }
        }

        /// <summary>
        /// The name a site is remembered under: its host, lower-cased, without the
        /// port and without a leading <c>www.</c>, or null when the address is not
        /// a site at all.
        ///
        /// Null is the important half. The start screen is a page this app
        /// generates rather than somewhere the user went (<see cref="Store.IsGenerated"/>
        /// knows both shapes of it), and neither it nor an <c>about:</c> page has a
        /// site whose settings could be meant. Callers treat null as "leave whatever
        /// is applied alone", which is what keeps a trip through the start screen
        /// from resetting the settings of the page on either side of it.
        /// </summary>
        public static string KeyFor(string url)
        {
            if (string.IsNullOrEmpty(url) || url == "-" || Store.IsGenerated(url))
            {
                return null;
            }

            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme <= 0)
            {
                return null;
            }

            string protocol = url.Substring(0, scheme).ToLowerInvariant();
            if (protocol != "http" && protocol != "https")
            {
                return null;
            }

            string rest = url.Substring(scheme + 3);
            int end = rest.Length;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c == '/' || c == '?' || c == '#')
                {
                    end = i;
                    break;
                }
            }

            string host = rest.Substring(0, end);

            // Credentials, then the port. Both are part of the authority and
            // neither is part of the site's name.
            int at = host.LastIndexOf('@');
            if (at >= 0)
            {
                host = host.Substring(at + 1);
            }

            int colon = host.IndexOf(':');
            if (colon >= 0)
            {
                host = host.Substring(0, colon);
            }

            host = host.ToLowerInvariant().TrimEnd('.');
            if (host.StartsWith("www.", StringComparison.Ordinal) && host.Length > 4)
            {
                host = host.Substring(4);
            }

            return host.Length == 0 ? null : host;
        }

        /// <summary>
        /// The rule that answers for this address, or null.
        ///
        /// A rule matches its own host and anything under it, so one saved on
        /// <c>instagram.com</c> (which is where <c>www.instagram.com</c> is
        /// remembered) also answers for <c>i.instagram.com</c>. The match is on
        /// whole labels — <c>notinstagram.com</c> is a different site and a
        /// suffix test that let it through would hand a stranger's site the
        /// settings meant for this one. The most specific rule wins, so a site
        /// with a rule of its own is not overruled by one on its parent.
        /// </summary>
        public static SiteRule For(string url)
        {
            string host = KeyFor(url);
            if (host == null)
            {
                return null;
            }

            SiteRule best = null;
            for (int i = 0; i < Rules.Count; i++)
            {
                string site = Rules[i].Site;
                bool covers = host.Length == site.Length
                    ? string.Equals(host, site, StringComparison.Ordinal)
                    : host.Length > site.Length &&
                      host[host.Length - site.Length - 1] == '.' &&
                      host.EndsWith(site, StringComparison.Ordinal);

                if (covers && (best == null || site.Length > best.Site.Length))
                {
                    best = Rules[i];
                }
            }

            return best;
        }

        /// <summary>
        /// What this address should be loaded with: the site's own answers where it
        /// has them, the browser-wide settings everywhere else.
        /// </summary>
        public static void Effective(string url, int defaultUa, bool defaultImages,
                                     out int ua, out bool images)
        {
            ua = defaultUa;
            images = defaultImages;

            SiteRule rule = For(url);
            if (rule == null)
            {
                return;
            }

            if (rule.Ua != SiteRule.Unset)
            {
                ua = rule.Ua;
            }

            if (rule.Images != SiteRule.Unset)
            {
                images = rule.Images == 1;
            }
        }

        /// <summary>
        /// Whether this address goes through the proxy: the site's own answer where
        /// it has one, the browser-wide switch everywhere else. Separate from
        /// <see cref="Effective"/> because only one of the six builds can ask it.
        /// </summary>
        public static bool ProxyFor(string url, bool defaultProxy)
        {
            SiteRule rule = For(url);
            return rule == null || rule.Proxy == SiteRule.Unset ? defaultProxy : rule.Proxy == 1;
        }

        /// <summary>Remembers a UA preset for this site. Returns the rule it landed on.</summary>
        public static SiteRule SetUa(string url, int preset)
        {
            SiteRule rule = Claim(url);
            if (rule != null)
            {
                rule.Ua = preset;
                Save();
            }

            return rule;
        }

        /// <summary>Remembers the images switch for this site. Returns the rule it landed on.</summary>
        public static SiteRule SetImages(string url, bool on)
        {
            SiteRule rule = Claim(url);
            if (rule != null)
            {
                rule.Images = on ? 1 : 0;
                Save();
            }

            return rule;
        }

        /// <summary>Remembers the proxy switch for this site. Returns the rule it landed on.</summary>
        public static SiteRule SetProxy(string url, bool on)
        {
            SiteRule rule = Claim(url);
            if (rule != null)
            {
                rule.Proxy = on ? 1 : 0;
                Save();
            }

            return rule;
        }

        /// <summary>
        /// Drops whatever was remembered for this site. Returns the rule that was
        /// removed, or null when the site had none — which is what lets the caller
        /// say so rather than claiming to have forgotten something.
        /// </summary>
        public static SiteRule Forget(string url)
        {
            SiteRule rule = For(url);
            if (rule == null)
            {
                return null;
            }

            Rules.Remove(rule);
            Save();
            return rule;
        }

        /// <summary>
        /// Where the switch action goes from the address given, or null when there
        /// is nowhere to go.
        ///
        /// It goes round the favourites, in the order they sit on the start screen:
        /// from a site that has a favourite, to the site of the next favourite after
        /// it, wrapping round at the end; from a site that has none, to the first.
        /// Two favourites on one site count as one stop. Each stop opens the last
        /// page the history has on that site, so a return to Spotify lands where
        /// Spotify was left and not on the tile's address — the tile is the answer
        /// only for a site never visited.
        ///
        /// Issue #75 asked for a way between <em>two</em> sites, and the first
        /// shape of this walked the history instead: from B the first entry not on
        /// B is the page on A the user came from, and from A it is B again, so one
        /// action alternated between the two sites somebody is going back and forth
        /// between. The reporter's third site is what that shape cannot reach — the
        /// history is always ordered by what was just left, so the two most recent
        /// sites trade places for ever and the third never comes up. The favourites
        /// are the list he already keeps, in an order he can see, so they are the
        /// round. The history walk stays as the answer when there is no round to go:
        /// no favourites, or every favourite on the site already open. From the
        /// start screen (a null address, since it is on no site) it opens the most
        /// recent site either way.
        /// </summary>
        public static Bookmark OtherSite(IList<Bookmark> favourites, IList<Bookmark> history, string currentUrl)
        {
            string here = KeyFor(currentUrl);
            if (here != null && favourites != null)
            {
                // The distinct sites among the favourites, in tile order.
                var sites = new List<string>();
                for (int i = 0; i < favourites.Count; i++)
                {
                    string site = KeyFor(favourites[i].Url);
                    if (site != null && !sites.Contains(site))
                    {
                        sites.Add(site);
                    }
                }

                int at = sites.IndexOf(here);
                if (sites.Count > (at >= 0 ? 1 : 0))
                {
                    string next = sites[at < 0 ? 0 : (at + 1) % sites.Count];
                    return LastOn(history, next) ?? LastOn(favourites, next);
                }
            }

            return OtherSite(history, currentUrl);
        }

        /// <summary>The first entry in the list that is on the site named, or null.</summary>
        private static Bookmark LastOn(IList<Bookmark> list, string site)
        {
            if (list == null)
            {
                return null;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(KeyFor(list[i].Url), site, StringComparison.Ordinal))
                {
                    return list[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The most recent entry in the history that is on a different site from
        /// the address given, or null when there is none. The first shape of the
        /// switch (see above), and still what it does when there is no round of
        /// favourites to go: the history is kept most-recent-first, so from a page
        /// on B the first entry that is not on B is the page on A the user came
        /// from — and once they are on A the first entry that is not on A is the
        /// page on B they just left. From the start screen (a null address, since
        /// it is on no site) it opens the most recent site instead.
        /// </summary>
        public static Bookmark OtherSite(IList<Bookmark> history, string currentUrl)
        {
            if (history == null)
            {
                return null;
            }

            string here = KeyFor(currentUrl);
            for (int i = 0; i < history.Count; i++)
            {
                string there = KeyFor(history[i].Url);
                if (there == null)
                {
                    continue;
                }

                if (here == null || !string.Equals(there, here, StringComparison.Ordinal))
                {
                    return history[i];
                }
            }

            return null;
        }

        /// <summary>The rules as the diagnostics report shows them.</summary>
        public static string Dump()
        {
            if (Rules.Count == 0)
            {
                return "(none — images, identity and proxy are remembered per site once you set them)";
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < Rules.Count; i++)
            {
                SiteRule rule = Rules[i];
                text.Append(rule.Site);
                if (rule.Ua != SiteRule.Unset)
                {
                    text.Append("   identity ").Append(rule.Ua.ToString(CultureInfo.InvariantCulture));
                }

                if (rule.Images != SiteRule.Unset)
                {
                    text.Append("   images ").Append(rule.Images == 1 ? "on" : "off");
                }

                if (rule.Proxy != SiteRule.Unset)
                {
                    text.Append("   proxy ").Append(rule.Proxy == 1 ? "on" : "off");
                }

                text.Append("\n");
            }

            return text.ToString();
        }

        /// <summary>
        /// The rule to write this address's answer into: the one already covering
        /// it if there is one, a new one for its own host otherwise.
        ///
        /// Editing the covering rule rather than making a narrower one is what
        /// keeps the file honest about what the user did. Somebody who set an
        /// identity on <c>instagram.com</c> and later flips images while sitting on
        /// <c>i.instagram.com</c> means the same site, and two rules — one of them
        /// silently overruling the other — is not something the screen could ever
        /// explain to them.
        /// </summary>
        private static SiteRule Claim(string url)
        {
            SiteRule existing = For(url);
            if (existing != null)
            {
                return existing;
            }

            string host = KeyFor(url);
            if (host == null || Rules.Count >= RuleLimit)
            {
                return null;
            }

            var rule = new SiteRule(host);
            Rules.Add(rule);
            return rule;
        }

        private static void Load()
        {
            Rules.Clear();
            if (_dir == null)
            {
                return;
            }

            try
            {
                string path = Path.Combine(_dir, FileName);
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(path))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 1 || parts[0].Length == 0)
                    {
                        continue;
                    }

                    var rule = new SiteRule(parts[0].ToLowerInvariant());
                    for (int i = 1; i < parts.Length; i++)
                    {
                        int split = parts[i].IndexOf('=');
                        if (split <= 0)
                        {
                            continue;
                        }

                        string name = parts[i].Substring(0, split);
                        int value;
                        if (!int.TryParse(parts[i].Substring(split + 1), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out value))
                        {
                            continue;
                        }

                        if (name == "ua")
                        {
                            rule.Ua = value;
                        }
                        else if (name == "images")
                        {
                            // Anything that is not a 0 or a 1 is a file we did not
                            // write; leave the field unset rather than inventing an
                            // answer for it.
                            rule.Images = value == 0 || value == 1 ? value : SiteRule.Unset;
                        }
                        else if (name == "proxy")
                        {
                            rule.Proxy = value == 0 || value == 1 ? value : SiteRule.Unset;
                        }
                    }

                    // A line that says nothing is a rule that does nothing, and
                    // keeping it would show the site on the report as remembered.
                    if (!rule.IsEmpty)
                    {
                        Rules.Add(rule);
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("site rules: cannot read " + FileName + ": " + ex.Message);
            }
        }

        private static void Save()
        {
            if (_dir == null)
            {
                return;
            }

            // A rule emptied by hand (every field taken back to the default) is not
            // a rule; dropping it here is what stops the file, and the report, from
            // filling up with sites that are remembered as "nothing in particular".
            for (int i = Rules.Count - 1; i >= 0; i--)
            {
                if (Rules[i].IsEmpty)
                {
                    Rules.RemoveAt(i);
                }
            }

            try
            {
                var lines = new List<string>();
                foreach (SiteRule rule in Rules)
                {
                    var line = new System.Text.StringBuilder(rule.Site);
                    if (rule.Ua != SiteRule.Unset)
                    {
                        line.Append('\t').Append("ua=")
                            .Append(rule.Ua.ToString(CultureInfo.InvariantCulture));
                    }

                    if (rule.Images != SiteRule.Unset)
                    {
                        line.Append('\t').Append("images=")
                            .Append(rule.Images.ToString(CultureInfo.InvariantCulture));
                    }

                    if (rule.Proxy != SiteRule.Unset)
                    {
                        line.Append('\t').Append("proxy=")
                            .Append(rule.Proxy.ToString(CultureInfo.InvariantCulture));
                    }

                    lines.Add(line.ToString());
                }

                File.WriteAllLines(Path.Combine(_dir, FileName), lines.ToArray());
            }
            catch (Exception ex)
            {
                DiagLog.Add("site rules: cannot write " + FileName + ": " + ex.Message);
            }
        }
    }
}
