namespace Overscan
{
    /// <summary>
    /// Evas key names as delivered by a Samsung TV remote. These are the names
    /// that arrive in <see cref="ElmSharp.EvasKeyEventArgs.KeyName"/>; they are
    /// NOT the same as the web-runtime keycodes used by .wgt apps.
    /// </summary>
    internal static class RemoteKeys
    {
        public const string Left = "Left";
        public const string Right = "Right";
        public const string Up = "Up";
        public const string Down = "Down";

        public const string Ok = "Return";
        public const string OkKeypad = "KP_Enter";

        // XF86Back is the remote's Return/Back key. ElmSharp also surfaces it as
        // Window.BackButtonPressed, but only when the key is grabbed.
        public const string Back = "XF86Back";
        public const string Exit = "XF86Exit";
        public const string Menu = "XF86Menu";
        public const string Info = "XF86Info";
        public const string Search = "XF86Search";

        /// <summary>The "123"/More button; what the emulator's remote panel sends.</summary>
        public const string More = "XF86More";

        public const string ChannelUp = "XF86RaiseChannel";
        public const string ChannelDown = "XF86LowerChannel";

        public const string Num0 = "0";
        public const string Num1 = "1";
        public const string Num2 = "2";
        public const string Num3 = "3";
        public const string Num4 = "4";
        public const string Num5 = "5";
        public const string Num6 = "6";
        public const string Num7 = "7";
        public const string Num8 = "8";
        public const string Num9 = "9";

        /// <summary>
        /// Keys the window must explicitly grab, otherwise the TV's own
        /// launcher/shell consumes them before the app sees them. D-pad and OK
        /// are delivered without a grab.
        /// </summary>
        public static readonly string[] Grabbed =
        {
            Back, Menu, Info, Search, More, ChannelUp, ChannelDown,
            Num0, Num1, Num2, Num3, Num4, Num5, Num6, Num7, Num8, Num9,
        };
    }
}
