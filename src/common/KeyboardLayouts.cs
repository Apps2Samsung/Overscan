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
    /// Three things can change what the grid shows: the layout (QWERTY/AZERTY/…),
    /// <see cref="Shift"/> and <see cref="Symbols"/>. Only the layout is
    /// remembered across launches — a shift or symbol page is a momentary state,
    /// and coming back to a keyboard stuck on punctuation would be baffling.
    ///
    /// Every grid has the same shape — four rows of 10 plus the action row —
    /// because the keyboards build their cells once and only swap the labels when
    /// the grid changes. A grid of a different shape would leave those cells
    /// pointing at keys that are no longer there.
    /// </summary>
    internal static class KeyboardLayouts
    {
        /// <summary>The grid key that cycles to the next layout.</summary>
        public const string CycleKey = "layout";

        /// <summary>Switches the letter rows between lower and upper case.</summary>
        public const string ShiftKey = "shift";

        /// <summary>Switches the letter rows for punctuation and symbols.</summary>
        public const string SymbolsKey = "sym";

        /// <summary>Saves what was typed as the page to open at start-up.</summary>
        public const string StartPageKey = "start";

        private const string SettingKey = "keyboardLayout";

        private static readonly string[] LayoutNames = { "QWERTY", "AZERTY", "QWERTZ", "ABCDEF" };

        private static readonly string[][][] Grids =
        {
            Build("qwertyuiop", "asdfghjkl-", "zxcvbnm_?="),
            Build("azertyuiop", "qsdfghjklm", "wxcvbn-_?="),
            Build("qwertzuiop", "asdfghjkl-", "yxcvbnm_?="),
            Build("abcdefghij", "klmnopqrst", "uvwxyz-_?="),
        };

        private static readonly string[][][] ShiftedGrids = BuildShifted();

        /// <summary>
        /// The symbol page. `@` also sits on the action row, where it is always one
        /// press away — an address or an e-mail address needs it constantly, and
        /// having to find the symbol page for it was issue #15.
        /// </summary>
        private static readonly string[][] SymbolGrid =
        {
            Split("@#$%&*()[]"),
            Split("-_+=/\\|~^\u20AC"),
            Split(":;\"'<>,.?!"),
            new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
            ActionRow(),
        };

        private static int _index = -1;

        /// <summary>Name of the current layout, as shown on the cycle key.</summary>
        public static string Name
        {
            get { return LayoutNames[Index]; }
        }

        /// <summary>True while the letter rows show capitals.</summary>
        public static bool Shift { get; private set; }

        /// <summary>True while the symbol page is showing instead of the letters.</summary>
        public static bool Symbols { get; private set; }

        /// <summary>The current grid: rows of key names, top row first.</summary>
        public static string[][] Rows
        {
            get
            {
                if (Symbols)
                {
                    return SymbolGrid;
                }

                return Shift ? ShiftedGrids[Index] : Grids[Index];
            }
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
        /// Back to plain letters. Called when the keyboard opens: shift and the
        /// symbol page are momentary state, and a keyboard that comes up on
        /// punctuation because that is where it was left is baffling.
        /// </summary>
        public static string[][] Reset()
        {
            Shift = false;
            Symbols = false;
            return Rows;
        }

        /// <summary>Turns capitals on or off and returns the grid to show.</summary>
        public static string[][] ToggleShift()
        {
            Shift = !Shift;
            return Rows;
        }

        /// <summary>Swaps between the letters and the symbol page.</summary>
        public static string[][] ToggleSymbols()
        {
            Symbols = !Symbols;
            return Rows;
        }

        /// <summary>
        /// A capital is nearly always wanted once — the first letter of a name, or
        /// the one upper-case character a password rule demands — so shift releases
        /// itself after the key it applied to, the way a phone keyboard does.
        /// Returns the grid to show, or null when nothing changed.
        /// </summary>
        public static string[][] ReleaseShift()
        {
            if (!Shift)
            {
                return null;
            }

            Shift = false;
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
                ActionRow(),
            };
        }

        /// <summary>
        /// The upper-case twin of every layout. Only the three letter rows are
        /// touched: the digits and the action row have no capitals, and shifting
        /// the pad characters would give keys the layout does not have.
        /// </summary>
        private static string[][][] BuildShifted()
        {
            var shifted = new string[Grids.Length][][];
            for (int g = 0; g < Grids.Length; g++)
            {
                string[][] source = Grids[g];
                var rows = new string[source.Length][];
                for (int r = 0; r < source.Length; r++)
                {
                    if (r > 2)
                    {
                        rows[r] = source[r];
                        continue;
                    }

                    rows[r] = new string[source[r].Length];
                    for (int c = 0; c < source[r].Length; c++)
                    {
                        rows[r][c] = source[r][c].ToUpperInvariant();
                    }
                }

                shifted[g] = rows;
            }

            return shifted;
        }

        /// <summary>
        /// The bottom row, identical on every grid. `@` sits here rather than on the
        /// symbol page because signing in to anything needs it; `shift` and `sym`
        /// are next to each other so the two ways of reaching a different character
        /// are in one place.
        /// </summary>
        private static string[] ActionRow()
        {
            return new[]
            {
                ".", "/", ":", "@", "space", ".com", "back", "clear",
                ShiftKey, SymbolsKey, StartPageKey, "GO", "close", CycleKey,
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
