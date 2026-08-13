using System;
using System.Text;

namespace Overscan
{
    internal static class Urls
    {
        public const string Home = "https://duckduckgo.com/";

        /// <summary>
        /// Turns whatever was typed into something loadable: a bare host becomes
        /// https, anything with a space becomes a search.
        /// </summary>
        public static string Normalize(string input)
        {
            string trimmed = (input ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return Home;
            }

            if (trimmed.IndexOf("://", StringComparison.Ordinal) > 0)
            {
                return trimmed;
            }

            bool looksLikeHost = trimmed.IndexOf(' ') < 0 && trimmed.IndexOf('.') > 0;
            return looksLikeHost
                ? "https://" + trimmed
                : "https://duckduckgo.com/?q=" + Uri.EscapeDataString(trimmed);
        }

        /// <summary>
        /// Quotes a string for embedding in injected JavaScript. Typed text reaches
        /// the page through a script, so an unescaped quote would break the script
        /// (and be an injection point).
        /// </summary>
        public static string JsString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control chars, plus the two separators JS treats as line
                        // terminators inside string literals.
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.Append('"').ToString();
        }
    }
}
