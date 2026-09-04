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

            // 6. The request trail: key, header lookup, cap, dump, cost.
            Check(RequestTrail.Key("https://audio-fa.scdn.co/audio/8f3a9c1e2b?token=x") == "audio-fa.scdn.co/audio",
                  "key is host plus the first segment, never the track hash or the query");
            Check(RequestTrail.Key("https://spclient.wg.spotify.com/ad-logic/state/config") == "spclient.wg.spotify.com/ad-logic",
                  "key stops at the first segment");
            Check(RequestTrail.Key("https://open.spotify.com/") == "open.spotify.com/" &&
                  RequestTrail.Key("https://open.spotify.com") == "open.spotify.com/" &&
                  RequestTrail.Key("https://open.spotify.com?x=1") == "open.spotify.com/",
                  "a bare host, with or without slash or query, is one key");
            Check(RequestTrail.Key("data:text/html;charset=utf-8,%3Chtml%3E") == "data:" &&
                  RequestTrail.Key("about:blank") == "about:",
                  "the start screen's data: page is one line, not a 3 KB key");
            Check(RequestTrail.Key(null) == "(empty)" && RequestTrail.Key("nonsense") == "(no scheme)", "nothing and garbage do not throw");

            var headers = new System.Collections.Generic.Dictionary<string, string>
            {
                { "sec-fetch-dest", "audio" }, { "Range", "bytes=0-" }, { "Accept", "*/*" },
            };
            Check(RequestTrail.HeaderOf(headers, "Sec-Fetch-Dest") == "audio", "header lookup ignores the engine's casing");
            Check(RequestTrail.HeaderOf(headers, "Range") == "bytes=0-", "header lookup finds an exact name first");
            Check(RequestTrail.HeaderOf(headers, "Sec-Fetch-Mode") == null && RequestTrail.HeaderOf(null, "Range") == null,
                  "a missing header, or no headers, is null");

            Check(RequestTrail.Dump() == "(no requests yet)", "an empty trail says so");
            RequestTrail.Record("https://audio-fa.scdn.co/audio/aaa?x", "GET", "audio", "no-cors", false, false);
            RequestTrail.Record("https://audio-fa.scdn.co/audio/bbb", "GET", "empty", "cors", true, false);
            RequestTrail.Record("https://www.googletagmanager.com/gtm.js", "GET", "script", "no-cors", false, true);
            RequestTrail.Record("data:text/html,x", null, null, null, false, false);
            string dump = RequestTrail.Dump();
            Check(dump.StartsWith("4 requests, 3 distinct host/path lines\n", StringComparison.Ordinal), "the dump counts requests and lines: " + dump.Split('\n')[0]);
            Check(dump.IndexOf("audio-fa.scdn.co/audio", StringComparison.Ordinal) < dump.IndexOf("googletagmanager.com/gtm.js", StringComparison.Ordinal),
                  "most requested line first");
            Check(dump.IndexOf("audio,empty", StringComparison.Ordinal) >= 0 && dump.IndexOf("some", StringComparison.Ordinal) >= 0,
                  "one line carries every dest seen and says the range header was on some requests");
            Check(dump.IndexOf("https://audio-fa.scdn.co/audio/aaa  ", StringComparison.Ordinal) < 0 &&
                  dump.IndexOf("https://audio-fa.scdn.co/audio/aaa\n", StringComparison.Ordinal) >= 0 ||
                  dump.IndexOf("  ·  https://audio-fa.scdn.co/audio/aaa", StringComparison.Ordinal) >= 0,
                  "the sample is the first URL seen, without its query");
            Check(System.Text.RegularExpressions.Regex.IsMatch(dump, @"(?m)^1 +1 +GET +script\b"),
                  "a request we answered is counted on its line");
            Check(dump.IndexOf("count  answered  method", StringComparison.Ordinal) >= 0,
                  "the column is \"answered\": a silenced ad never left the TV either");

            for (int i = 0; i < RequestTrail.MaxKeys + 50; i++)
            {
                RequestTrail.Record("https://host" + i + ".example/p", "GET", null, null, false, false);
            }

            dump = RequestTrail.Dump();
            Check(dump.IndexOf(RequestTrail.MaxKeys + " distinct host/path lines, 53 past the " + RequestTrail.MaxKeys + "-line cap", StringComparison.Ordinal) >= 0,
                  "the cap holds and the overflow is counted: " + dump.Split('\n')[0]);

            var trailClock = Stopwatch.StartNew();
            for (int i = 0; i < Rounds; i++)
            {
                RequestTrail.Record("https://audio-fa.scdn.co/audio/" + (i & 7) + "?x=1", "GET", "empty", "cors", true, false);
            }

            trailClock.Stop();
            double perRecordUs = trailClock.Elapsed.TotalMilliseconds * 1000.0 / Rounds;
            Check(perRecordUs < 20.0, "a trail record costs " + perRecordUs.ToString("0.00") + " us on this box");

            // 7. The hosts answered with a second of silence rather than refused
            //    (AdSilence.cs). The whole point of the split is that the ad's host
            //    and the music's host are different, so the checks that matter are
            //    the negative ones.
            Check(AdSilence.Matches("adstudio-assets.scdn.co"), "the ad host is silenced");
            Check(AdSilence.Matches("ADSTUDIO-ASSETS.SCDN.CO."), "case and a trailing dot do not matter to it either");
            Check(AdSilence.Matches("eu.adstudio-assets.scdn.co"), "a subdomain of it is silenced too");
            string[] mustNotBeSilenced =
            {
                // The music, on the host build-f295172's report actually saw it on,
                // and the two neighbours a careless parent-domain rule would take
                // with it. A clip served in place of any of these is silence where
                // the song should be, which is worse than the ad.
                "audio-ak.spotifycdn.com", "audio-fa.spotifycdn.com", "seektables.spotifycdn.com",
                "i.scdn.co", "encore.scdn.co", "misc.scdn.co", "www.scdn.co", "scdn.co",
                "open.spotify.com", "open.spotifycdn.com", "spclient.wg.spotify.com",
                "gae2-spclient.spotify.com", "video-cf.spotifycdn.com",
                // A longer name that merely ends in the listed one.
                "notadstudio-assets.scdn.co",
            };
            foreach (string host in mustNotBeSilenced)
            {
                Check(!AdSilence.Matches(host), "not silenced: " + host);
            }

            Check(!hosts.Matches("adstudio-assets.scdn.co"),
                  "the ad host is not also in adhosts.txt, where update.sh would overwrite it");

            byte[] clip = AdSilence.Clip;
            Check(clip.Length == 4160, "the clip is 4,160 bytes: " + clip.Length);
            Check(AdSilence.ContentType == "audio/mpeg", "served as audio/mpeg");
            int frames = 0;
            bool framing = true;
            for (int i = 0; i < clip.Length; i += 104)
            {
                frames++;
                if (clip[i] != 0xFF || clip[i + 1] != 0xFB || clip[i + 2] != 0x10 || clip[i + 3] != 0xC4)
                {
                    framing = false;
                }

                for (int j = i + 4; j < i + 104; j++)
                {
                    if (clip[j] != 0)
                    {
                        framing = false;
                    }
                }
            }

            Check(framing && frames == 40, "40 MPEG-1 Layer III frames, each a header and a hundred zero bytes");
            Check(Math.Abs(frames * 1152.0 / 44100.0 - 1.045) < 0.01,
                  "which is the second uBlock found the player needs: " + (frames * 1152.0 / 44100.0).ToString("0.000") + "s");
            Check(ReferenceEquals(AdSilence.Clip, clip), "the same array is handed back every time, never rebuilt per request");

            Console.WriteLine();
            Console.WriteLine("adblock: the clip decoding is tools/adsilence/run.sh, in a real decoder.");
            Console.WriteLine(_failures == 0 ? "adblock: all checks passed" : "adblock: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }
    }
}
