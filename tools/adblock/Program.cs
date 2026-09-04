using System;
using System.Diagnostics;

namespace Overscan
{
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

        private static int Main()
        {
            // 1. The list is there and is the size it should be.
            AdHosts hosts = AdHosts.LoadEmbedded();
            Check(hosts.Count > 3000 && hosts.Count < 10000, "embedded list loaded: " + hosts.Count + " hosts");

            // 2. Host extraction.
            Check(AdHosts.HostOf("https://Ads.DoubleClick.net/x/y?z=1#f") == "ads.doubleclick.net", "scheme, path, query, fragment, case");
            Check(AdHosts.HostOf("http://user:pw@host.example:8080/") == "host.example", "credentials and port stripped");
            Check(AdHosts.HostOf("https://host.example?x=1") == "host.example", "query straight after the host");
            Check(AdHosts.HostOf("https://[2001:db8::1]:443/p") == "2001:db8::1", "IPv6 literal keeps its colons");
            Check(AdHosts.HostOf("data:text/html;charset=utf-8,%3Chtml%3E") == string.Empty, "data: has no host");
            Check(AdHosts.HostOf("about:blank") == string.Empty, "about: has no host");
            Check(AdHosts.HostOf("/relative/path") == string.Empty, "relative reference has no host");
            Check(AdHosts.HostOf(null) == string.Empty && AdHosts.HostOf(string.Empty) == string.Empty, "null and empty");

            // 3. Matching: the host, everything under it, none of the look-alikes.
            Check(hosts.Matches("doubleclick.net"), "a listed host");
            Check(hosts.Matches("ad.doubleclick.net"), "a subdomain of a listed host");
            Check(hosts.Matches("stats.g.doubleclick.net"), "two levels under a listed host");
            Check(hosts.Matches("DOUBLECLICK.NET."), "case and a trailing dot do not matter");
            Check(!hosts.Matches("notdoubleclick.net"), "a longer name that merely ends in a listed one");
            Check(!hosts.Matches("net"), "a top-level domain is never a candidate");
            Check(!hosts.Matches(string.Empty) && !hosts.Matches(null), "nothing matches nothing");
            var tiny = new AdHosts(new[] { "# comment", string.Empty, "com", "ads.example.com" });
            Check(tiny.Count == 1 && !tiny.Matches("www.example.com") && tiny.Matches("x.ads.example.com"),
                  "comments, blanks and a bare TLD are skipped; a listed subdomain refuses only its own subtree");

            // 4. The sites this app is used on stay reachable.
            string[] mustPass =
            {
                "www.instagram.com", "i.instagram.com", "static.cdninstagram.com", "scontent.cdninstagram.com",
                "open.spotify.com", "www.spotify.com", "accounts.spotify.com", "i.scdn.co", "api.spotify.com",
                "accounts.google.com", "www.google.com", "www.gstatic.com", "fonts.googleapis.com", "apis.google.com",
                "www.youtube.com", "youtube.com", "i.ytimg.com", "www.facebook.com", "static.xx.fbcdn.net",
                "www.netflix.com", "www.reddit.com", "www.wikipedia.org", "tv4h.weebly.com",
            };
            foreach (string host in mustPass)
            {
                Check(!hosts.Matches(host), "not refused: " + host);
            }

            // 5. Cost per lookup.
            string[] mix =
            {
                "www.instagram.com", "stats.g.doubleclick.net", "static.cdninstagram.com", "open.spotify.com",
                "securepubads.g.doubleclick.net", "i.scdn.co", "www.googletagmanager.com", "scontent.cdninstagram.com",
            };
            const int Rounds = 100000;
            int refused = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Rounds; i++)
            {
                for (int j = 0; j < mix.Length; j++)
                {
                    if (hosts.Matches(AdHosts.HostOf("https://" + mix[j] + "/some/path?with=query")))
                    {
                        refused++;
                    }
                }
            }

            clock.Stop();
            double perLookupUs = clock.Elapsed.TotalMilliseconds * 1000.0 / (Rounds * mix.Length);
            Check(perLookupUs < 20.0, "parse + lookup costs " + perLookupUs.ToString("0.00") + " us each on this box (" + refused + " refused)");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "adblock: all checks passed" : "adblock: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }
    }
}
