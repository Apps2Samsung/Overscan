using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Overscan
{
    /// <summary>
    /// The compiled-in list of ad and tracker hosts, and the one question ever
    /// asked of it: is this host, or any domain above it, on the list.
    /// </summary>
    /// <remarks>
    /// Deliberately not a rule language. No wildcards, no paths, no regular
    /// expressions: a host either is on the list or has a parent that is. That is
    /// what keeps the lookup a handful of dictionary probes, and this lookup runs
    /// on every request the engine makes, on a TV, on a thread that is not ours
    /// (see <see cref="NuiAdBlock"/>). Cosmetic filtering and per-site exceptions
    /// are different features and are out of scope by decision (issue #37).
    ///
    /// The list itself is <c>adhosts.txt</c> beside this file, embedded into the
    /// NUI package only; <c>tools/adhosts/update.sh</c> refreshes it and says where
    /// it comes from. Nothing here touches the engine, which is what lets
    /// <c>tools/adblock/run.sh</c> compile this file on a desktop and hold it to
    /// its answers before a TV ever sees it.
    /// </remarks>
    internal sealed class AdHosts
    {
        /// <summary>The embedded resource's logical name, set in OverscanNui.csproj.</summary>
        public const string ResourceName = "Overscan.adhosts.txt";

        private readonly HashSet<string> _hosts = new HashSet<string>(StringComparer.Ordinal);

        public AdHosts(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();

                // A host without a dot would be a top-level domain, and matching
                // one of those would refuse every request there is. The list has
                // none; this makes sure a bad edit cannot put one in.
                if (line.Length == 0 || line[0] == '#' || line.IndexOf('.') < 0)
                {
                    continue;
                }

                _hosts.Add(line.ToLowerInvariant());
            }
        }

        public int Count
        {
            get { return _hosts.Count; }
        }

        public static AdHosts LoadEmbedded()
        {
            Assembly assembly = typeof(AdHosts).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException("embedded resource missing", ResourceName);
                }

                var lines = new List<string>();
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }

                return new AdHosts(lines);
            }
        }

        /// <summary>
        /// Whether the host, or any domain above it, is on the list. So a listed
        /// <c>doubleclick.net</c> also refuses <c>stats.g.doubleclick.net</c>, and a
        /// listed <c>ads.example.com</c> refuses nothing else under
        /// <c>example.com</c>. The walk stops before the last label: a top-level
        /// domain is never a candidate.
        /// </summary>
        public bool Matches(string host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return false;
            }

            string candidate = host.ToLowerInvariant().TrimEnd('.');
            while (true)
            {
                int dot = candidate.IndexOf('.');
                if (dot < 0)
                {
                    return false;
                }

                if (_hosts.Contains(candidate))
                {
                    return true;
                }

                candidate = candidate.Substring(dot + 1);
            }
        }

        /// <summary>
        /// The host of an absolute URL, lower-cased, without port or credentials;
        /// empty for anything that has no host (<c>data:</c>, <c>about:</c>, a
        /// relative reference). Written out rather than through <see cref="Uri"/>
        /// because this runs per request and <see cref="Uri"/> both allocates
        /// heavily and throws on the malformed addresses pages actually emit.
        /// </summary>
        public static string HostOf(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            int start = url.IndexOf("://", StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += 3;
            int end = url.Length;
            for (int i = start; i < url.Length; i++)
            {
                char c = url[i];
                if (c == '/' || c == '?' || c == '#')
                {
                    end = i;
                    break;
                }
            }

            string authority = url.Substring(start, end - start);

            int at = authority.LastIndexOf('@');
            if (at >= 0)
            {
                authority = authority.Substring(at + 1);
            }

            // An IPv6 literal keeps its colons; anything else loses the port.
            if (authority.Length > 0 && authority[0] == '[')
            {
                int close = authority.IndexOf(']');
                return close > 0 ? authority.Substring(1, close - 1).ToLowerInvariant() : string.Empty;
            }

            int colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                authority = authority.Substring(0, colon);
            }

            return authority.ToLowerInvariant();
        }
    }
}
