using ElmSharp;

namespace Overscan
{
    /// <summary>
    /// One palette and one text helper for every overlay.
    ///
    /// ElmSharp gives us `Rectangle`, `Label` (EFL markup) and `Image` — no rounded
    /// corners, no blur, no gradients. So the look comes from a restrained palette,
    /// generous spacing, and 1-2px "edges" drawn as a slightly lighter rectangle
    /// behind a darker fill. Sizes are chosen for a 1080p panel viewed from a sofa:
    /// nothing below ~24px, and the primary text much larger than a desktop would
    /// use.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Ink = Color.FromRgba(234, 238, 246, 255);
        public static readonly Color InkMuted = Color.FromRgba(150, 158, 174, 255);
        public static readonly Color Panel = Color.FromRgba(13, 15, 20, 232);
        public static readonly Color PanelDeep = Color.FromRgba(9, 10, 14, 244);
        public static readonly Color Edge = Color.FromRgba(58, 63, 78, 255);
        public static readonly Color Accent = Color.FromRgba(72, 148, 255, 255);
        public static readonly Color Positive = Color.FromRgba(52, 168, 108, 255);
        public static readonly Color Negative = Color.FromRgba(196, 84, 78, 255);
        public static readonly Color KeyFill = Color.FromRgba(32, 35, 43, 255);
        public static readonly Color KeyFillAlt = Color.FromRgba(44, 48, 58, 255);

        public const int BarHeight = 72;
        public const int Pad = 24;

        /// <summary>
        /// EFL markup. Labels parse their text, so anything user-supplied (a URL, a
        /// page title) has to be escaped or a stray `<` swallows the rest.
        /// </summary>
        public static string Text(string value, int size, Color color, bool bold = false, string align = null)
        {
            string escaped = (value ?? string.Empty)
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\n", "<br/>");

            string open = "<font_size=" + size + "><color=#" + Hex(color) + ">";
            string close = "</color></font_size>";

            if (bold)
            {
                open += "<b>";
                close = "</b>" + close;
            }

            if (align != null)
            {
                open = "<align=" + align + ">" + open;
                close += "</align>";
            }

            return open + escaped + close;
        }

        private static string Hex(Color color)
        {
            return color.R.ToString("x2") + color.G.ToString("x2") + color.B.ToString("x2");
        }
    }
}
