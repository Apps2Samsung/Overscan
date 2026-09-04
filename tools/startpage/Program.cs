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

        /// <summary>
        /// What the NUI engine reports as the URL of a page loaded from a string,
        /// as seen on issue #20's set: the page itself, percent-encoded, behind a
        /// data: prefix. The exact escaping does not matter to the store; the
        /// scheme does.
        /// </summary>
        private static string AsEngineUrl(string html)
        {
            return "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        }

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
            {
                _failures++;
            }
        }

        private static int Main(string[] args)
        {
            string dir = args[0];
            Directory.CreateDirectory(dir);

            // 1. Neither shape of the generated page is ever kept.
            Store.Init(dir);
            Store.RecordVisit(HomePage.BaseUrl, "Overscan");
            Store.RecordVisit(AsEngineUrl(HomePage.Build(Store.AllFavourites, Store.RecentHistory, "https://x/")), "Overscan");
            Store.RecordVisit("https://example.com/", "Example");
            Check(Store.RecentHistory.Count == 1 && Store.RecentHistory[0].Url == "https://example.com/",
                  "only the real visit is in history (" + Store.RecentHistory.Count + " entries)");
            Check(!Store.ToggleFavourite(AsEngineUrl("<html></html>"), "Overscan") && Store.AllFavourites.Count == 0,
                  "a data: page cannot be made a favourite");
            Check(!Store.ToggleFavourite(HomePage.BaseUrl, "Overscan") && Store.AllFavourites.Count == 0,
                  "the start marker cannot be made a favourite");

            // 2. A history file from before the fix is healed on load.
            string healDir = Path.Combine(dir, "heal");
            Directory.CreateDirectory(healDir);
            string nested = AsEngineUrl("<html>one</html>");
            nested = AsEngineUrl("<a href='" + nested + "'>two</a>");
            File.WriteAllLines(Path.Combine(healDir, "history.tsv"), new[]
            {
                "https://a.example/\tA",
                nested + "\tOverscan",
                "DATA:text/html,upper\tOverscan",
                HomePage.BaseUrl + "\tOverscan",
                "https://b.example/\tB",
            });
            File.WriteAllLines(Path.Combine(healDir, "favourites.tsv"), new[]
            {
                AsEngineUrl("<html>fav</html>") + "\tOverscan",
                "https://c.example/\tC",
            });
            DiagLog.Lines.Clear();
            Store.Init(healDir);
            Check(Store.RecentHistory.Count == 2 && Store.AllFavourites.Count == 1,
                  "generated pages dropped on load (history " + Store.RecentHistory.Count +
                  ", favourites " + Store.AllFavourites.Count + ")");
            Check(File.ReadAllLines(Path.Combine(healDir, "history.tsv")).Length == 2 &&
                  File.ReadAllLines(Path.Combine(healDir, "favourites.tsv")).Length == 1,
                  "both files written back clean");
            Check(DiagLog.Lines.Exists(l => l.Contains("dropped 4 generated")),
                  "the log says how many were dropped: " + string.Join(" | ", DiagLog.Lines));

            // 3. The bug itself: start screens in a row must not feed on each other.
            string growDir = Path.Combine(dir, "grow");
            Directory.CreateDirectory(growDir);
            Store.Init(growDir);
            Store.RecordVisit("https://www.instagram.com/", "Instagram");
            int first = HomePage.Build(Store.AllFavourites, Store.RecentHistory, "https://x/").Length;
            int last = first;
            for (int i = 0; i < 12; i++)
            {
                string html = HomePage.Build(Store.AllFavourites, Store.RecentHistory, "https://x/");
                last = html.Length;
                Store.RecordVisit(AsEngineUrl(html), "Overscan");
            }

            Check(last == first, "twelve start screens later the page is still " + last + " chars (was " + first + ")");
            Check(Store.RecentHistory.Count == 1, "and history still holds the one real visit");

            // 4. Sign-in waypoints are passed through, not visited. The three shapes
            //    are Instagram's, straight from issue #53's report; the long one is
            //    the same recaptcha page with its real-length token.
            string wayDir = Path.Combine(dir, "waypoints");
            Directory.CreateDirectory(wayDir);
            Store.Init(wayDir);
            Store.RecordVisit("https://www.instagram.com/", "Instagram");
            Store.RecordVisit("https://www.instagram.com/auth_platform/recaptcha/?apc=Q8HeCgM0", "Instagram");
            Store.RecordVisit("https://www.instagram.com/auth_platform/codeentry/?apc=AdqowNHA", "Instagram");
            Store.RecordVisit("https://www.instagram.com/accounts/onetap/", "Instagram");
            Store.RecordVisit("https://accounts.google.com/o/oauth2/v2/auth?client_id=1", "Sign in");
            Store.RecordVisit("https://example.com/Login?next=%2F", "Log in");
            Store.RecordVisit("https://example.com/page#/challenge", "Fragment is not path");
            Store.RecordVisit("https://example.com/p?q=" + new string('x', 1100), "Token");
            Store.RecordVisit("https://login.example.com/", "Host is not path");
            Store.RecordVisit("https://example.com/daily-challenge/", "Whole segments only");
            Store.RecordVisit("https://www.instagram.com/reels/DcrJqxByZTE/", "Instagram");
            Check(Store.RecentHistory.Count == 5,
                  "five real visits kept, six waypoints skipped (" + Store.RecentHistory.Count + " entries)");
            Check(Store.RecentHistory[0].Url == "https://www.instagram.com/reels/DcrJqxByZTE/" &&
                  Store.RecentHistory[1].Url == "https://example.com/daily-challenge/" &&
                  Store.RecentHistory[2].Url == "https://login.example.com/" &&
                  Store.RecentHistory[3].Url == "https://example.com/page#/challenge" &&
                  Store.RecentHistory[4].Url == "https://www.instagram.com/",
                  "and they are the right five");
            Check(Store.ToggleFavourite("https://www.instagram.com/accounts/onetap/", "Instagram") &&
                  Store.AllFavourites.Count == 1,
                  "a waypoint can still be kept as a favourite on purpose");

            // 5. A history file with waypoints in it is healed on load; favourites are not touched.
            string wayHealDir = Path.Combine(dir, "waypoints-heal");
            Directory.CreateDirectory(wayHealDir);
            File.WriteAllLines(Path.Combine(wayHealDir, "history.tsv"), new[]
            {
                "https://www.instagram.com/\tInstagram",
                "https://www.instagram.com/auth_platform/recaptcha/?apc=Q8HeCgM0\tInstagram",
                "https://www.instagram.com/accounts/onetap/\tInstagram",
                "https://www.instagram.com/reels/DcrJqxByZTE/\tInstagram",
            });
            File.WriteAllLines(Path.Combine(wayHealDir, "favourites.tsv"), new[]
            {
                "https://www.instagram.com/accounts/onetap/\tInstagram",
            });
            DiagLog.Lines.Clear();
            Store.Init(wayHealDir);
            Check(Store.RecentHistory.Count == 2 && Store.AllFavourites.Count == 1,
                  "waypoints dropped from history only (history " + Store.RecentHistory.Count +
                  ", favourites " + Store.AllFavourites.Count + ")");
            Check(File.ReadAllLines(Path.Combine(wayHealDir, "history.tsv")).Length == 2,
                  "history written back clean");
            Check(DiagLog.Lines.Exists(l => l.Contains("dropped 2 sign-in")),
                  "the log says how many: " + string.Join(" | ", DiagLog.Lines));

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "startpage: all checks passed" : "startpage: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }
    }
}
