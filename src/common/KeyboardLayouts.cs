namespace Overscan
{
    /// <summary>
    /// The key grids the on-screen keyboard can show, and which one is current.
    ///
    /// Shared by the ElmSharp and NUI keyboards so both get the same layouts and
    /// the same remembered choice. QWERTY first because that is what almost every
    /// user's muscle memory expects; the alphabetical grid the browser started with
    /// is still there, last in the cycle.
    ///
    /// Every grid has the same shape — five rows of 10, 10, 10, 10 and 11 keys —
    /// because the keyboards build their cells once and only swap the labels when
    /// the layout changes. A grid of a different shape would leave those cells
    /// pointing at keys that are no longer there.
    /// </summary>
    internal static class KeyboardLayouts
    {
        /// <summary>The grid key that cycles to the next layout.</summary>
        public const string CycleKey = "layout";

        private const string SettingKey = "keyboardLayout";

        private static readonly string[] LayoutNames = { "QWERTY", "AZERTY", "QWERTZ", "ABCDEF" };

        private static readonly string[][][] Grids =
        {
            Build("qwertyuiop", "asdfghjkl-", "zxcvbnm_?="),
            Build("azertyuiop", "qsdfghjklm", "wxcvbn-_?="),
            Build("qwertzuiop", "asdfghjkl-", "yxcvbnm_?="),
            Build("abcdefghij", "klmnopqrst", "uvwxyz-_?="),
        };

        private static int _index = -1;

        /// <summary>Name of the current layout, as shown on the cycle key.</summary>
        public static string Name
        {
            get { return LayoutNames[Index]; }
        }

        /// <summary>The current grid: rows of key names, top row first.</summary>
        public static string[][] Rows
        {
            get { return Grids[Index]; }
        }

        private static int Index
        {
            get
            {
                if (_index < 0)
                {
                    // Read on first use rather than at start-up: Store.Init runs
                    // after the type may first be touched.
                    int saved = Store.GetInt(SettingKey, 0);
                    _index = saved >= 0 && saved < Grids.Length ? saved : 0;
                }

                return _index;
            }
        }

        /// <summary>Moves to the next layout, remembers it, and returns its grid.</summary>
        public static string[][] Next()
        {
            _index = (Index + 1) % Grids.Length;
            Store.Set(SettingKey, _index);
            DiagLog.Add("keyboard layout " + Name);
            return Rows;
        }

        /// <summary>
        /// Builds a grid from its three letter rows. The digits and the action row
        /// are the same whatever the letters do, so they are not repeated per layout.
        /// </summary>
        private static string[][] Build(string top, string home, string bottom)
        {
            return new[]
            {
                Split(top),
                Split(home),
                Split(bottom),
                new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
                new[] { ".", "/", ":", "&", "space", ".com", "back", "clear", "GO", "close", CycleKey },
            };
        }

        private static string[] Split(string row)
        {
            var keys = new string[row.Length];
            for (int i = 0; i < row.Length; i++)
            {
                keys[i] = row[i].ToString();
            }

            return keys;
        }
    }
}
