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
            // Issue #75. History is most-recent-first, so the first entry that is
            // not on this site is the site the user came from — and from there, the
            // first that is not on *that* one is the site they left. One action
            // therefore alternates between the two, which is how the question was
            // asked.
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

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "siterules: all checks passed" : "siterules: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }
    }
}
