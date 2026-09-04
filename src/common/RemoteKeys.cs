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

        // ------------------------------------------------------------ menu keys
        //
        // A slim Samsung remote (The Frame, TM2360E and relatives) has no number
        // keys at all, so every one of the Num* bindings below is unreachable on
        // it — see issue #27. These are the buttons such a remote *does* have and
        // that this app has no other use for. They all open the on-screen menu,
        // which is the only way those functions can be reached without a numpad.
        //
        // Which name a given button actually sends is not documented and differs
        // between remote generations, so the menu answers to all of them rather
        // than to the one that happens to work on the sets we can test. An
        // unrecognised key is not silently dropped either: its name is written to
        // the hints card, so a user with a remote we have never seen can read the
        // name off the screen and tell us.
        public const string PlayBack = "XF86PlayBack";
        public const string AudioPlay = "XF86AudioPlay";
        public const string AudioPause = "XF86AudioPause";
        public const string AudioPlayPause = "XF86AudioPlayPause";
        public const string Tools = "XF86Tools";
        public const string SimpleMenu = "XF86SimpleMenu";
        public const string SysMenu = "XF86SysMenu";
        public const string PlainMenu = "Menu";

        /// <summary>
        /// Buttons whose only job is to open the on-screen menu.
        /// </summary>
        public static readonly string[] MenuKeys =
        {
            Menu, More, Tools, SimpleMenu, SysMenu, PlainMenu,
        };

        /// <summary>
        /// Transport buttons, which also open the menu — but only as a fallback.
        /// Some slim remotes have no menu-ish button at all and these are all that
        /// is left; a remote that has both should not need them.
        /// </summary>
        /// <remarks>
        /// A browser that swallows Play/Pause while a video is playing is its own
        /// kind of broken, so these stop opening the menu the moment the user routes
        /// keys to the page (key 4) — that switch already exists for exactly this
        /// sort of collision, and the page then gets them untouched.
        /// </remarks>
        public static readonly string[] MediaKeys =
        {
            PlayBack, AudioPlay, AudioPause, AudioPlayPause,
        };

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
            Tools, SimpleMenu, SysMenu,
            PlayBack, AudioPlay, AudioPause, AudioPlayPause,
        };

        /// <summary>
        /// True for a button whose only job here is to open the menu. Kept as a
        /// method rather than a set lookup so the caller needs no allocation on
        /// the key path.
        /// </summary>
        public static bool IsMenuKey(string key)
        {
            return Contains(MenuKeys, key);
        }

        /// <summary>True for a transport button — see <see cref="MediaKeys"/>.</summary>
        public static bool IsMediaKey(string key)
        {
            return Contains(MediaKeys, key);
        }

        /// <summary>
        /// True for a button whose press is a reason to show the address bar:
        /// going back, opening the menu, or one of the numbered shortcuts, each of
        /// which changes something the bar then names. Everything else leaves the
        /// bar alone — the pointer, the channel rocker, and any button this app has
        /// no binding for (volume, mute, whatever a remote we have never seen sends).
        /// </summary>
        /// <remarks>
        /// A list of the buttons that <em>do</em> show it, not "everything but the
        /// pointer". The first version was the exclusion, and that is how the
        /// rocker got through: `build-5490157` took the bar off the pointer keys,
        /// and issue #20's reporter, who reads a feed by paging it with channel
        /// up/down, still had the bar come back on every page and asked again.
        /// A button somebody holds down to move through a page must never be the
        /// thing that keeps the bar alive, and the only way to be sure of that for
        /// buttons nobody has tested is to name the ones that qualify.
        /// </remarks>
        public static bool IsChromeKey(string key)
        {
            if (key == Back || key == Info)
            {
                return true;
            }

            if (key == Num0 || key == Num1 || key == Num2 || key == Num3 || key == Num4 ||
                key == Num5 || key == Num6 || key == Num7 || key == Num8 || key == Num9)
            {
                return true;
            }

            return Contains(MenuKeys, key) || Contains(MediaKeys, key);
        }

        private static bool Contains(string[] keys, string key)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (keys[i] == key)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
