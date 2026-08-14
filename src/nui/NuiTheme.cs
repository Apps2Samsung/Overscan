using Tizen.NUI;

namespace Overscan
{
    /// <summary>
    /// The ElmSharp <see cref="Theme"/> palette, expressed in NUI colours (0-1
    /// floats rather than bytes) so both builds look like the same product. NUI can
    /// do rounded corners, so panels here get a small radius the ElmSharp side
    /// cannot have.
    /// </summary>
    internal static class NuiTheme
    {
        public static readonly Color Ink = new Color(0.918f, 0.933f, 0.965f, 1f);
        public static readonly Color InkMuted = new Color(0.588f, 0.620f, 0.682f, 1f);
        public static readonly Color Panel = new Color(0.051f, 0.059f, 0.078f, 0.910f);
        public static readonly Color PanelDeep = new Color(0.035f, 0.039f, 0.055f, 0.957f);
        public static readonly Color Edge = new Color(0.227f, 0.247f, 0.306f, 1f);
        public static readonly Color Accent = new Color(0.282f, 0.580f, 1f, 1f);
        public static readonly Color Positive = new Color(0.204f, 0.659f, 0.424f, 1f);
        public static readonly Color Negative = new Color(0.769f, 0.329f, 0.306f, 1f);
        public static readonly Color KeyFill = new Color(0.125f, 0.137f, 0.169f, 1f);
        public static readonly Color KeyFillAlt = new Color(0.173f, 0.188f, 0.227f, 1f);

        public const int BarHeight = 72;
        public const int Pad = 24;
        public const float Radius = 12f;
    }
}
