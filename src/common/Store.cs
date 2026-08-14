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
            DiagLog.Add("store: " + Favourites.Count + " favourites, " + History.Count +
                        " history, " + Settings.Count + " settings");
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
            if (string.IsNullOrEmpty(url) || url == "-")
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

        public static void RecordVisit(string url, string title)
        {
            if (string.IsNullOrEmpty(url) || url == "-" || url.StartsWith("about:", StringComparison.Ordinal))
            {
                return;
            }

            // The home page is generated, not visited.
            if (url.StartsWith(HomePage.BaseUrl, StringComparison.Ordinal))
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

        private static int IndexOf(List<Bookmark> list, string url)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Url, url, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
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
