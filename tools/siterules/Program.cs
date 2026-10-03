using System;
using System.Collections.Generic;
using System.IO;

namespace Overscan
{
    /// <summary>Stands in for the app's diagnostics log; the harness reads it back.</summary>
    internal static class DiagLog
    {
        public static readonly List<string> Lines = new List<string>();

        public static void Add(string line)
        {
            Lines.Add(line);
        }
    }

    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
            {
                _failures++;
            }
        }

        /// <summary>
        /// What the NUI engine reports for a page loaded from a string: the page
        /// itself, percent-encoded, behind a data: prefix. The start screen arrives
        /// at KeyFor in this shape on one build and as the marker URL on the other,
        /// and both have to come back as "no site".
        /// </summary>
        private static string AsEngineUrl(string html)
        {
            return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        }

        private static string Key(string url)
        {
            return SiteRules.KeyFor(url) ?? "(none)";
        }

        private static int Main(string[] args)
        {
            string root = args[0];
            Directory.CreateDirectory(root);

            // ---------------------------------------------------------- 1. names
            //
            // What a site is called. Getting this wrong in either direction is a
            // whole class of fault: too narrow and a rule stops applying the moment
            // a site moves you to another of its own hosts, too wide and one site's
            // settings land on somebody else's.
            Check(Key("https://www.instagram.com/") == "instagram.com", "www. is not part of the name");
            Check(Key("https://INSTAGRAM.com/x") == "instagram.com", "the name is lower-cased");
            Check(Key("https://open.spotify.com:443/?x=1#y") == "open.spotify.com",
                  "port, query and fragment are not part of the name");
            Check(Key("https://user:pw@example.com/") == "example.com",
                  "credentials are not part of the name");
            Check(Key("https://example.com./") == "example.com", "a trailing root dot is dropped");
            Check(Key("http://192.168.1.5:8081/") == "192.168.1.5", "an address is a site like any other");

            // The addresses that are not sites. Both shapes of this app's own start
            // screen are in here: issue #53 is what it cost the last time only one
            // of them was recognised.
            Check(SiteRules.KeyFor(null) == null, "null is not a site");
            Check(SiteRules.KeyFor("-") == null, "the empty-url placeholder is not a site");
            Check(SiteRules.KeyFor("about:blank") == null, "about: is not a site");
            Check(SiteRules.KeyFor(HomePage.BaseUrl) == null, "the start-screen marker is not a site");
            Check(SiteRules.KeyFor(AsEngineUrl("<html></html>")) == null,
                  "a data: page is not a site");
            Check(SiteRules.KeyFor("file:///etc/passwd") == null, "file: is not a site");

            // ------------------------------------------------- 2. what a rule covers
            string dir = Path.Combine(root, "cover");
            Directory.CreateDirectory(dir);
            SiteRules.Init(dir);
            SiteRules.SetUa("https://www.instagram.com/", 1);

            Check(SiteRules.For("https://www.instagram.com/x") != null, "the site itself matches");
            Check(SiteRules.For("https://instagram.com/") != null, "and without the www.");
            Check(SiteRules.For("https://i.instagram.com/api") != null, "a host under it matches");
            Check(SiteRules.For("https://notinstagram.com/") == null,
                  "a look-alike does NOT match — the boundary is a whole label");
            Check(SiteRules.For("https://instagram.com.evil.test/") == null,
                  "and neither does a site that merely starts with the name");
            Check(SiteRules.For("https://example.com/") == null, "an unrelated site has no rule");

            // The most specific rule wins where two of them cover the same host.
            // Reachable in this order only: a rule made on the subdomain first,
            // and one on the site afterwards, which does not yet cover it. The
            // other order edits the covering rule instead — check 3 below.
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            SiteRules.Init(nested);
            SiteRules.SetUa("https://i.instagram.com/", 3);
            SiteRules.SetUa("https://www.instagram.com/", 1);
            Check(SiteRules.All.Count == 2 &&
                  SiteRules.For("https://i.instagram.com/").Ua == 3 &&
                  SiteRules.For("https://www.instagram.com/").Ua == 1,
                  "the more specific rule wins where both cover a host");

            // ------------------------------------------------- 3. what a rule says
            //
            // Unset is not "off". A site remembered only for its identity still
            // follows the browser-wide images switch when that is flipped later,
            // which is the difference between remembering a choice and freezing
            // every other one alongside it.
            string prefs = Path.Combine(root, "prefs");
            Directory.CreateDirectory(prefs);
            SiteRules.Init(prefs);
            SiteRules.SetUa("https://open.spotify.com/", 2);

            int ua;
            bool images;
            SiteRules.Effective("https://open.spotify.com/", 0, false, out ua, out images);
            Check(ua == 2 && !images, "the site's identity is used and the browser's images are kept");

            SiteRules.Effective("https://open.spotify.com/", 0, true, out ua, out images);
            Check(ua == 2 && images, "and the browser's images the other way round too");

            SiteRules.SetImages("https://open.spotify.com/", true);
            SiteRules.Effective("https://open.spotify.com/", 0, false, out ua, out images);
            Check(ua == 2 && images, "once set, the site's images win over the browser's");

            SiteRules.Effective("https://example.com/", 4, false, out ua, out images);
            Check(ua == 4 && !images, "a site with no rule gets the browser's answers unchanged");

            SiteRules.Effective(HomePage.BaseUrl, 4, true, out ua, out images);
            Check(ua == 4 && images, "and so does an address that is on no site at all");

            // A second switch on the same site edits the same rule rather than
            // adding another one beside it.
            Check(SiteRules.All.Count == 1, "one site set twice is still one rule");

            // A rule is claimed at the level that already covers the host, so a
            // switch flipped on a subdomain does not quietly shadow the rule the
            // user made on the site.
            SiteRule onSub = SiteRules.SetImages("https://accounts.spotify.com/", false);
            Check(onSub != null && onSub.Site == "accounts.spotify.com" && SiteRules.All.Count == 2,
                  "a host no rule covers gets one of its own");
            SiteRule again = SiteRules.SetUa("https://widget.open.spotify.com/", 3);
            Check(again != null && again.Site == "open.spotify.com" && SiteRules.All.Count == 2,
                  "a host an existing rule covers edits that rule instead of shadowing it");

            // ------------------------------------------------------ 4. on the disk
            SiteRules.Init(prefs);
            Check(SiteRules.All.Count == 2, "the rules come back from the file");
            SiteRule spotify = SiteRules.For("https://open.spotify.com/");
            Check(spotify != null && spotify.Ua == 3 && spotify.Images == 1,
                  "with both fields intact");
            SiteRule accounts = SiteRules.For("https://accounts.spotify.com/");
            Check(accounts != null && accounts.Ua == SiteRule.Unset && accounts.Images == 0,
                  "and a field nobody set stays unset across a save");

            // Forgetting is a real operation and says whether it did anything, so
            // the screen can tell "cleared" from "there was nothing to clear".
            Check(SiteRules.Forget("https://example.com/") == null, "forgetting an unremembered site says so");
            Check(SiteRules.Forget("https://open.spotify.com/") != null && SiteRules.All.Count == 1,
                  "forgetting a remembered one removes it");
            SiteRules.Init(prefs);
            Check(SiteRules.All.Count == 1 && SiteRules.For("https://open.spotify.com/") == null,
                  "and the removal survives a restart");

            // A file another build wrote, or one somebody edited by hand. None of
            // it may stop the browser: an unreadable preference is a forgotten
            // preference, never a launch that does not happen.
            string junk = Path.Combine(root, "junk");
            Directory.CreateDirectory(junk);
            File.WriteAllLines(Path.Combine(junk, "sites.tsv"), new[]
            {
                "example.com\tua=1\timages=0",
                string.Empty,
                "\tua=1",
                "nothing.test",
                "weird.test\tua=notanumber\timages=7\tfuture=1",
                "UPPER.TEST\timages=1",
            });
            DiagLog.Lines.Clear();
            SiteRules.Init(junk);
            Check(SiteRules.All.Count == 2,
                  "blank, nameless and empty lines are dropped (" + SiteRules.All.Count + " kept)");
            Check(SiteRules.For("https://example.com/") != null &&
                  SiteRules.For("https://upper.test/") != null,
                  "the two real ones survive, the second lower-cased");
            Check(SiteRules.For("https://weird.test/") == null,
                  "a line whose every field is unreadable is not a rule");

            // ------------------------------------------------- 5. the other site
            //
            // Issue #75, and #100's complaint about it. With no favourites the
            // switch walks the history: it is most-recent-first, so the first entry
            // that is not on this site is the site the user came from — and from
            // there, the first that is not on *that* one is the site they left. One
            // action therefore alternates between the two, which is how #75 was
            // asked, and which is also why a third site never came up: the two most
            // recent sites trade places for ever. With favourites it goes round
            // them instead, in tile order, each at the last page seen on that site.
            var none = new List<Bookmark>();
            var history = new List<Bookmark>
            {
                new Bookmark("https://open.spotify.com/album/2", "Spotify"),
                new Bookmark("https://open.spotify.com/album/1", "Spotify"),
                new Bookmark("https://www.instagram.com/reels/x/", "Instagram"),
                new Bookmark("https://www.instagram.com/", "Instagram"),
            };

            Bookmark other = SiteRules.OtherSite(history, "https://open.spotify.com/album/2");
            Check(other != null && other.Url == "https://www.instagram.com/reels/x/",
                  "from Spotify it opens the last Instagram page");
            Bookmark back = SiteRules.OtherSite(history, other.Url);
            Check(back != null && back.Url == "https://open.spotify.com/album/2",
                  "and from there it comes back to the last Spotify page");
            Check(SiteRules.OtherSite(history, "https://i.instagram.com/api") != null &&
                  SiteRules.OtherSite(history, "https://i.instagram.com/api").Url ==
                      "https://open.spotify.com/album/2",
                  "another host of the same site counts as the same site");
            Bookmark fromHome = SiteRules.OtherSite(history, HomePage.BaseUrl);
            Check(fromHome != null && fromHome.Url == "https://open.spotify.com/album/2",
                  "from the start screen it opens the most recent site");
            Check(SiteRules.OtherSite(new List<Bookmark>(), "https://a.test/") == null,
                  "an empty history has nowhere to go");
            Check(SiteRules.OtherSite(new List<Bookmark>
                  {
                      new Bookmark("https://a.test/2", "A"),
                      new Bookmark("https://a.test/1", "A"),
                  }, "https://a.test/2") == null,
                  "and neither has one that has only ever seen this site");
            Check(SiteRules.OtherSite(new List<Bookmark>
                  {
                      new Bookmark(AsEngineUrl("<html></html>"), "Overscan"),
                      new Bookmark("https://b.test/", "B"),
                  }, "https://a.test/").Url == "https://b.test/",
                  "a generated page in the history is skipped, not switched to");
            Check(SiteRules.OtherSite(none, history, "https://open.spotify.com/album/2").Url ==
                      "https://www.instagram.com/reels/x/" &&
                  SiteRules.OtherSite(null, history, other.Url).Url == "https://open.spotify.com/album/2",
                  "with no favourites the three-argument form is the same walk");

            // The round. Three favourites in tile order, two of them on sites the
            // history knows; a return lands on the page that was left there, not on
            // the tile, and the one site never visited opens at its tile.
            var favourites = new List<Bookmark>
            {
                new Bookmark("https://www.instagram.com/", "Instagram"),
                new Bookmark("https://open.spotify.com/", "Spotify"),
                new Bookmark("https://open.spotify.com/playlist/9", "Spotify again"),
                new Bookmark("https://www.tiktok.com/", "TikTok"),
            };
            Bookmark step1 = SiteRules.OtherSite(favourites, history, "https://www.instagram.com/reels/x/");
            Check(step1 != null && step1.Url == "https://open.spotify.com/album/2",
                  "from Instagram the round goes to Spotify, at the page left there");
            Bookmark step2 = SiteRules.OtherSite(favourites, history, step1.Url);
            Check(step2 != null && step2.Url == "https://www.tiktok.com/",
                  "from Spotify it goes past the second Spotify tile to TikTok, at its tile (never visited)");
            Bookmark step3 = SiteRules.OtherSite(favourites, history, "https://www.tiktok.com/@someone/live");
            Check(step3 != null && step3.Url == "https://www.instagram.com/reels/x/",
                  "from TikTok it wraps round to Instagram, at the page left there");
            Check(SiteRules.OtherSite(favourites, history, "https://www.google.com/search?q=x").Url ==
                      "https://www.instagram.com/reels/x/",
                  "from a site with no favourite it goes to the first favourite's site");
            Check(SiteRules.OtherSite(favourites, history, HomePage.BaseUrl).Url == "https://open.spotify.com/album/2",
                  "from the start screen it still opens the most recent site");
            Check(SiteRules.OtherSite(favourites, new List<Bookmark>(), "https://open.spotify.com/album/2").Url ==
                      "https://www.tiktok.com/",
                  "with no history at all each stop is its tile");
            var oneSite = new List<Bookmark>
            {
                new Bookmark("https://open.spotify.com/", "Spotify"),
                new Bookmark("https://open.spotify.com/playlist/9", "Spotify again"),
            };
            Check(SiteRules.OtherSite(oneSite, history, "https://open.spotify.com/album/2").Url ==
                      "https://www.instagram.com/reels/x/",
                  "every favourite on this site is no round, so it falls back to the site just left");
            Check(SiteRules.OtherSite(oneSite, history, "https://www.instagram.com/").Url ==
                      "https://open.spotify.com/album/2",
                  "and from elsewhere the one favourite site is where it goes");
            Check(SiteRules.OtherSite(new List<Bookmark> { new Bookmark(AsEngineUrl("<html></html>"), "Overscan") },
                                      history, "https://open.spotify.com/album/2").Url ==
                      "https://www.instagram.com/reels/x/",
                  "a favourite that is a generated page is not a stop");

            // ------------------------------------------------------- 6. the proxy
            //
            // Issue #97. The third field, held to the same promises as the other
            // two: unset follows the browser-wide switch, a set answer wins, it
            // survives the disk, and it is what keeps a rule alive on its own.
            string proxied = Path.Combine(root, "proxy");
            Directory.CreateDirectory(proxied);
            SiteRules.Init(proxied);
            SiteRules.SetUa("https://www.bbc.co.uk/", 1);
            Check(!SiteRules.ProxyFor("https://www.bbc.co.uk/iplayer", false) &&
                  SiteRules.ProxyFor("https://www.bbc.co.uk/iplayer", true),
                  "a rule that says nothing about the proxy follows the browser-wide switch");
            SiteRules.SetProxy("https://www.bbc.co.uk/", true);
            Check(SiteRules.ProxyFor("https://www.bbc.co.uk/iplayer", false),
                  "once set, the site's proxy wins over the browser's");
            Check(SiteRules.ProxyFor("https://ichef.bbc.co.uk/x.jpg", false),
                  "and covers the site's other hosts");
            Check(!SiteRules.ProxyFor("https://notbbc.co.uk/", false),
                  "but not a look-alike");
            Check(!SiteRules.ProxyFor(HomePage.BaseUrl, false) && SiteRules.ProxyFor(HomePage.BaseUrl, true),
                  "an address on no site gets the browser's answer");
            SiteRules.SetProxy("https://example.com/", false);
            Check(!SiteRules.ProxyFor("https://example.com/", true),
                  "a site switched off stays direct with the proxy on by default");
            SiteRules.SetUa("https://www.bbc.co.uk/", SiteRule.Unset);
            SiteRules.Init(proxied);
            SiteRule bbc = SiteRules.For("https://www.bbc.co.uk/");
            Check(bbc != null && bbc.Proxy == 1 && bbc.Ua == SiteRule.Unset,
                  "the proxy field survives the disk, and keeps a rule alive on its own");
            Check(SiteRules.For("https://example.com/") != null && SiteRules.For("https://example.com/").Proxy == 0,
                  "and so does a proxy switched off");
            Check(File.ReadAllText(Path.Combine(proxied, "sites.tsv")).Contains("bbc.co.uk\tproxy=1"),
                  "written as proxy=1 in the file");
            File.WriteAllLines(Path.Combine(proxied, "sites.tsv"), new[] { "a.test\tproxy=2", "b.test\tproxy=1\timages=0" });
            SiteRules.Init(proxied);
            Check(SiteRules.For("https://a.test/") == null && SiteRules.For("https://b.test/").Proxy == 1,
                  "a proxy value this build did not write is not an answer");
            Check(SiteRules.Dump().Contains("b.test   images off   proxy on"), "the report shows it");

            // The typed address. The whole risk is the login: it must reach the
            // engine's login call and nowhere else, because the diagnostics page
            // is open to anything on the LAN.
            string problem;
            ProxyAddress full = ProxyAddress.Parse("  HTTP://me:p@ss:w@Proxy.Example.com:8080/ ", out problem);
            Check(full != null && full.EngineUri == "http://proxy.example.com:8080",
                  "the engine is told scheme://host:port, lower-cased (" + (full == null ? problem : full.EngineUri) + ")");
            Check(full != null && full.User == "me" && full.Password == "p@ss:w",
                  "the login is lifted out, the password split at the first colon and the host at the last @");
            Check(full != null && !full.Display.Contains("p@ss") && full.Display.Contains("login set"),
                  "what is shown never has the password in it");
            Check(full != null && full.Entry == "http://me:p@ss:w@proxy.example.com:8080" &&
                  ProxyAddress.Parse(full.Entry, out problem).Entry == full.Entry,
                  "what is stored reads back as itself");
            ProxyAddress bare = ProxyAddress.Parse("10.0.0.2:3128", out problem);
            Check(bare != null && bare.EngineUri == "http://10.0.0.2:3128" && !bare.HasLogin,
                  "no scheme means http, and no login means none");
            ProxyAddress socks = ProxyAddress.Parse("socks://10.0.0.2:1080", out problem);
            Check(socks != null && socks.EngineUri == "socks5://10.0.0.2:1080",
                  "socks means SOCKS 5, not Chromium's SOCKS 4");
            ProxyAddress v6 = ProxyAddress.Parse("https://[fd00::1]:443", out problem);
            Check(v6 != null && v6.EngineUri == "https://[fd00::1]:443", "an IPv6 proxy keeps its brackets");
            Check(ProxyAddress.Parse("proxy.example.com", out problem) == null && problem.Contains("port"),
                  "a missing port is refused, not guessed");
            Check(ProxyAddress.Parse("socks5://u:p@10.0.0.2:1080", out problem) == null && problem.Contains("SOCKS"),
                  "a SOCKS login is refused: the engine would drop it without a word");
            Check(ProxyAddress.Parse("ftp://10.0.0.2:21", out problem) == null, "an unknown scheme is refused");
            Check(ProxyAddress.Parse("10.0.0.2:99999", out problem) == null, "a port past 65535 is refused");
            Check(ProxyAddress.Parse("10.0.0.2:80/path", out problem) == null, "a path is refused");
            Check(ProxyAddress.Parse("bad host:80", out problem) == null, "a space is refused");
            Check(ProxyAddress.Parse("@10.0.0.2:80", out problem) == null, "a login with no user is refused");
            Check(ProxyAddress.Parse("-x.test:80", out problem) == null, "a host starting with - is refused");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "siterules: all checks passed" : "siterules: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }
    }
}
