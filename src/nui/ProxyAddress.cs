using System;
using System.Globalization;

namespace Overscan
{
    /// <summary>
    /// The proxy a user typed, taken apart into what the engine is told and what
    /// it is never told. Issue #97.
    /// </summary>
    /// <remarks>
    /// The engine takes a Chromium proxy string (<c>scheme://host:port</c>) and,
    /// separately, one login it answers every proxy challenge with. A person types
    /// both as one address, <c>http://user:pass@host:port</c>, because that is how
    /// every proxy provider writes it down. So the login is lifted out here and
    /// never reaches <c>ProxyUrl</c>: Chromium's proxy grammar has no place for it
    /// and would read the <c>user:pass@</c> as part of the host.
    ///
    /// Everything shown (on screen, in the log, on the diagnostics page that any
    /// device on the LAN can open) uses <see cref="Display"/>, which has no
    /// password in it. <see cref="Entry"/> carries it and goes to the settings file
    /// and back into the keyboard, nowhere else.
    ///
    /// No Tizen types, so the harness can compile it as it ships.
    /// </remarks>
    internal sealed class ProxyAddress
    {
        private ProxyAddress(string scheme, string host, int port, string user, string password)
        {
            Scheme = scheme;
            Host = host;
            Port = port;
            User = user;
            Password = password;
        }

        /// <summary><c>http</c>, <c>https</c>, <c>socks4</c> or <c>socks5</c>.</summary>
        public string Scheme { get; private set; }

        /// <summary>Lower-cased; an IPv6 literal keeps its brackets.</summary>
        public string Host { get; private set; }

        public int Port { get; private set; }

        /// <summary>Null when the proxy needs no login.</summary>
        public string User { get; private set; }

        /// <summary>Null when the proxy needs no login; may be empty when it has a user.</summary>
        public string Password { get; private set; }

        public bool HasLogin
        {
            get { return User != null; }
        }

        /// <summary>What the engine is told: no login, ever.</summary>
        public string EngineUri
        {
            get { return Scheme + "://" + Host + ":" + Port.ToString(CultureInfo.InvariantCulture); }
        }

        /// <summary>What a screen or a report may show.</summary>
        public string Display
        {
            get { return EngineUri + (HasLogin ? "  (login set)" : string.Empty); }
        }

        /// <summary>The whole address, login included, as it is stored and edited.</summary>
        public string Entry
        {
            get
            {
                return HasLogin
                    ? Scheme + "://" + User + ":" + Password + "@" + Host + ":" +
                      Port.ToString(CultureInfo.InvariantCulture)
                    : EngineUri;
            }
        }

        /// <summary>
        /// Reads a typed proxy, or returns null with <paramref name="problem"/> set to
        /// a sentence short enough for the status bar.
        /// </summary>
        public static ProxyAddress Parse(string text, out string problem)
        {
            problem = null;
            string rest = (text ?? string.Empty).Trim();
            if (rest.Length == 0)
            {
                problem = "No proxy typed";
                return null;
            }

            if (rest.IndexOf(' ') >= 0)
            {
                problem = "A proxy address has no spaces in it";
                return null;
            }

            // No scheme means HTTP, which is what Chromium assumes too and what a
            // provider that writes just "host:port" means.
            string scheme = "http";
            int mark = rest.IndexOf("://", StringComparison.Ordinal);
            if (mark >= 0)
            {
                scheme = rest.Substring(0, mark).ToLowerInvariant();
                rest = rest.Substring(mark + 3);
            }

            // "socks" alone is SOCKS 4 to Chromium, and nobody typing it means that:
            // every SOCKS proxy a person is handed today is SOCKS 5.
            if (scheme == "socks" || scheme == "socks5h")
            {
                scheme = "socks5";
            }

            if (scheme != "http" && scheme != "https" && scheme != "socks4" && scheme != "socks5")
            {
                problem = "Proxy must start http://, https:// or socks5://";
                return null;
            }

            // A trailing slash is how a copied address usually ends. Anything past
            // it is a page, and a proxy has no pages.
            if (rest.EndsWith("/", StringComparison.Ordinal))
            {
                rest = rest.Substring(0, rest.Length - 1);
            }

            if (rest.IndexOf('/') >= 0 || rest.IndexOf('?') >= 0 || rest.IndexOf('#') >= 0)
            {
                problem = "A proxy is host:port, with no path after it";
                return null;
            }

            // The last @, not the first: a password may have one in it, a host
            // cannot.
            string user = null;
            string password = null;
            int at = rest.LastIndexOf('@');
            if (at >= 0)
            {
                string login = rest.Substring(0, at);
                rest = rest.Substring(at + 1);
                int colon = login.IndexOf(':');
                user = colon >= 0 ? login.Substring(0, colon) : login;
                password = colon >= 0 ? login.Substring(colon + 1) : string.Empty;
                if (user.Length == 0)
                {
                    problem = "The proxy login has no user name before the :";
                    return null;
                }

                // Chromium speaks SOCKS 5 without authentication only. The login
                // would be dropped without a word and the proxy would refuse every
                // connection, which from the sofa is "the proxy does not work".
                if (scheme == "socks4" || scheme == "socks5")
                {
                    problem = "The browser cannot log in to a SOCKS proxy, use its http:// address";
                    return null;
                }
            }

            string host;
            string portText;
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                int close = rest.IndexOf(']');
                if (close < 0 || close + 1 >= rest.Length || rest[close + 1] != ':')
                {
                    problem = "The proxy needs a port: [address]:port";
                    return null;
                }

                host = rest.Substring(0, close + 1);
                portText = rest.Substring(close + 2);
            }
            else
            {
                int colon = rest.LastIndexOf(':');
                if (colon < 0)
                {
                    // Required rather than defaulted. A proxy on its scheme's own
                    // port is rare, a forgotten port is not, and the engine's default
                    // for a missing one is not written down anywhere we can read.
                    problem = "The proxy needs a port: host:port";
                    return null;
                }

                host = rest.Substring(0, colon);
                portText = rest.Substring(colon + 1);
            }

            host = host.ToLowerInvariant();
            if (host.Length == 0 || !IsHost(host))
            {
                problem = "That is not a proxy host name";
                return null;
            }

            int port;
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
            {
                problem = "The proxy port is a number from 1 to 65535";
                return null;
            }

            return new ProxyAddress(scheme, host, port, user, password);
        }

        private static bool IsHost(string host)
        {
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                for (int i = 1; i < host.Length - 1; i++)
                {
                    char c = host[i];
                    if (!(c == ':' || c == '.' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                    {
                        return false;
                    }
                }

                return host.Length > 2 && host.EndsWith("]", StringComparison.Ordinal);
            }

            for (int i = 0; i < host.Length; i++)
            {
                char c = host[i];
                if (!(c == '.' || c == '-' || (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')))
                {
                    return false;
                }
            }

            return host[0] != '.' && host[0] != '-';
        }
    }
}
