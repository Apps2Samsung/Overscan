using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Overscan
{
    /// <summary>
    /// What the engine has asked for this run, folded to one line per host and
    /// first path segment, for the report. Issue #37's second question.
    /// </summary>
    /// <remarks>
    /// The first report from <c>build-6b29b8e</c> settled the hook: installed, a
    /// third of a millisecond per request, and 1 request in 823 refused on a
    /// Spotify session that played ads all the same. So the list is not where the
    /// ads are, and the report cannot say where they are, because nothing in it
    /// names a request. This does. It answers two things at once, and the fix for
    /// #37 depends on both:
    ///
    /// <list type="bullet">
    /// <item><b>Which hosts and paths the page actually uses.</b> Spotify's audio
    /// ads come off Spotify's own CDNs, the same ones the music comes off, so a
    /// host list can never separate them; the blockers that work on the web
    /// player match a path (<c>/mp3-ad/</c>, <c>/ad-logic/</c>) or answer the
    /// ad's audio with a second of silence. Which of those applies to this set's
    /// traffic is a question of what the URLs look like, and only the set can
    /// say.</item>
    /// <item><b>Whether the callback can tell an <c>&lt;audio&gt;</c> element's
    /// load from the music's XHR.</b> Chromium marks that in the request's
    /// <c>Sec-Fetch-Dest</c> header (<c>audio</c> against <c>empty</c>) and the
    /// interceptor exposes the request headers, but whether those headers are
    /// already on the request where the hook sits is not documented anywhere.
    /// The <c>dest=</c> column is the answer: a value means the engine's own type
    /// reaches us, a dash means it does not and the fix has to work from the
    /// URL alone.</item>
    /// </list>
    ///
    /// Runs on the interceptor's thread, so the same rules as
    /// <see cref="NuiAdBlock"/>: nothing here touches a view or the log, the map
    /// is concurrent, each entry is written under its own lock, and the whole
    /// thing is bounded (<see cref="MaxKeys"/> lines; everything past that is one
    /// counter) so a page that fans out to thousands of hosts cannot grow the
    /// process. The key is host plus the first path segment and never more: the
    /// second segment of an audio URL is the track's hash, and a key per track is
    /// a leak with a report attached. The sample kept per line is the first URL
    /// seen, without its query, because the shape of the address is what the
    /// next build is decided on.
    ///
    /// No engine types in this file, so <c>tools/adblock/run.sh</c> compiles it on
    /// the desktop and holds the key, the header lookup and the cap to their
    /// answers before a TV runs them.
    /// </remarks>
    internal static class RequestTrail
    {
        /// <summary>Distinct lines kept; the rest is counted, not listed.</summary>
        public const int MaxKeys = 300;

        private const int SampleLength = 120;

        private sealed class Entry
        {
            public long Count;
            public long Refused;
            public bool RangeSeen;
            public bool NoRangeSeen;
            public string Methods = string.Empty;
            public string Dests = string.Empty;
            public string Modes = string.Empty;
            public string Sample;
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private static long _overflow;
        private static long _total;

        /// <summary>
        /// One request. Every argument is a plain value already read off the
        /// interceptor; nothing here touches the request object, which after its
        /// answer may not be touched at all.
        /// </summary>
        public static void Record(string url, string method, string dest, string mode, bool hasRange, bool refused)
        {
            System.Threading.Interlocked.Increment(ref _total);

            string key = Key(url);
            Entry entry;
            if (!Entries.TryGetValue(key, out entry))
            {
                if (Entries.Count >= MaxKeys)
                {
                    System.Threading.Interlocked.Increment(ref _overflow);
                    return;
                }

                entry = Entries.GetOrAdd(key, new Entry());
            }

            lock (entry)
            {
                entry.Count++;
                if (refused)
                {
                    entry.Refused++;
                }

                if (hasRange)
                {
                    entry.RangeSeen = true;
                }
                else
                {
                    entry.NoRangeSeen = true;
                }

                entry.Methods = Add(entry.Methods, method);
                entry.Dests = Add(entry.Dests, dest);
                entry.Modes = Add(entry.Modes, mode);
                if (entry.Sample == null)
                {
                    entry.Sample = Sample(url);
                }
            }
        }

        /// <summary>
        /// <c>host/first-segment</c> for an absolute URL; the bare scheme
        /// (<c>data:</c>, <c>about:</c>) for anything without a host, so the start
        /// screen's own page is one line and not a 3 KB key.
        /// </summary>
        public static string Key(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return "(empty)";
            }

            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0)
            {
                int colon = url.IndexOf(':');
                return colon > 0 ? url.Substring(0, colon + 1) : "(no scheme)";
            }

            string host = AdHosts.HostOf(url);
            if (host.Length == 0)
            {
                host = "(no host)";
            }

            int pathStart = url.IndexOf('/', scheme + 3);
            if (pathStart < 0)
            {
                return host + "/";
            }

            int segmentEnd = url.Length;
            for (int i = pathStart + 1; i < url.Length; i++)
            {
                char c = url[i];
                if (c == '/' || c == '?' || c == '#')
                {
                    segmentEnd = i;
                    break;
                }
            }

            return host + url.Substring(pathStart, segmentEnd - pathStart);
        }

        /// <summary>
        /// A header by name, whatever case the engine hands it in. The toolkit's
        /// map is a plain dictionary with the engine's casing, which is not
        /// promised to be the canonical one.
        /// </summary>
        public static string HeaderOf(IDictionary<string, string> headers, string name)
        {
            if (headers == null)
            {
                return null;
            }

            string direct;
            if (headers.TryGetValue(name, out direct))
            {
                return direct;
            }

            foreach (KeyValuePair<string, string> pair in headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Value;
                }
            }

            return null;
        }

        /// <summary>The section for the report. Never throws.</summary>
        public static string Dump()
        {
            try
            {
                long total = System.Threading.Interlocked.Read(ref _total);
                long overflow = System.Threading.Interlocked.Read(ref _overflow);
                if (total == 0)
                {
                    return "(no requests yet)";
                }

                var lines = new List<KeyValuePair<string, Entry>>(Entries);
                lines.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

                var text = new StringBuilder();
                text.Append(total).Append(" requests, ").Append(lines.Count).Append(" distinct host/path lines");
                if (overflow > 0)
                {
                    text.Append(", ").Append(overflow).Append(" past the ").Append(MaxKeys).Append("-line cap and not listed");
                }

                text.Append('\n');
                // "answered" and not "refused": since build-f295172 the interceptor
                // answers an ad host with a silent clip and a 200 as well as refusing
                // a tracker with a 403, and both are requests that never left the TV.
                // Which of the two it was is the ad-block line's "silenced" count.
                text.Append("count  answered  method  dest        mode      range  host/first-segment  ·  first seen\n");
                foreach (KeyValuePair<string, Entry> line in lines)
                {
                    Entry e = line.Value;
                    string range;
                    lock (e)
                    {
                        range = e.RangeSeen ? (e.NoRangeSeen ? "some" : "yes") : "no";
                        text.Append(Pad(e.Count.ToString(), 5)).Append("  ")
                            .Append(Pad(e.Refused == 0 ? "-" : e.Refused.ToString(), 8)).Append("  ")
                            .Append(Pad(Blank(e.Methods), 6)).Append("  ")
                            .Append(Pad(Blank(e.Dests), 10)).Append("  ")
                            .Append(Pad(Blank(e.Modes), 8)).Append("  ")
                            .Append(Pad(range, 5)).Append("  ")
                            .Append(line.Key)
                            .Append("  ·  ").Append(e.Sample ?? string.Empty)
                            .Append('\n');
                    }
                }

                return text.ToString().TrimEnd('\n');
            }
            catch (Exception ex)
            {
                return "(request trail failed: " + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }

        private static string Add(string set, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return set;
            }

            if (set.Length == 0)
            {
                return value;
            }

            // A handful of distinct values per column, never more: this is a
            // set of methods or of fetch destinations, both of which Chromium
            // draws from a list shorter than ten.
            if ((set + ",").IndexOf(value + ",", StringComparison.Ordinal) == 0 ||
                set.IndexOf("," + value + ",", StringComparison.Ordinal) >= 0 ||
                set.EndsWith("," + value, StringComparison.Ordinal))
            {
                return set;
            }

            return set.Length > 40 ? set : set + "," + value;
        }

        private static string Blank(string set)
        {
            return set.Length == 0 ? "-" : set;
        }

        private static string Pad(string value, int width)
        {
            return value.Length >= width ? value : value + new string(' ', width - value.Length);
        }

        private static string Sample(string url)
        {
            int cut = url.Length;
            for (int i = 0; i < url.Length; i++)
            {
                char c = url[i];
                if (c == '?' || c == '#')
                {
                    cut = i;
                    break;
                }
            }

            string sample = url.Substring(0, cut);
            return sample.Length <= SampleLength ? sample : sample.Substring(0, SampleLength) + "…";
        }
    }
}
