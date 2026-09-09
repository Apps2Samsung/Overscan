using System;
using System.Collections.Generic;

namespace Overscan
{
    /// <summary>Stands in for the app's diagnostics log.</summary>
    internal static class DiagLog
    {
        public static readonly List<string> Lines = new List<string>();

        public static void Add(string line)
        {
            Lines.Add(line);
        }
    }

    /// <summary>Stands in for the settings store: the layout index, nothing else.</summary>
    internal static class Store
    {
        private static readonly Dictionary<string, int> Ints = new Dictionary<string, int>();

        public static int GetInt(string key, int fallback)
        {
            int value;
            return Ints.TryGetValue(key, out value) ? value : fallback;
        }

        public static void Set(string key, int value)
        {
            Ints[key] = value;
        }
    }

    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
            if (!ok)
            {
                _failures++;
            }
        }

        /// <summary>
        /// The key's centre on the screen in half-key units, the way the keyboards
        /// lay them out: rows centred, one pitch for every key.
        /// </summary>
        private static int Centre(string[][] rows, int row, int column)
        {
            int widest = 0;
            foreach (string[] r in rows)
            {
                widest = Math.Max(widest, r.Length);
            }

            return (widest - rows[row].Length) + (2 * column) + 1;
        }

        private static int Main()
        {
            // 1. Every grid keeps the shape the cells were built for.
            var grids = new List<KeyValuePair<string, string[][]>>();
            string[][] plain = KeyboardLayouts.Reset();
            for (int i = 0; i < 4; i++)
            {
                grids.Add(new KeyValuePair<string, string[][]>(KeyboardLayouts.Name, KeyboardLayouts.Rows));
                grids.Add(new KeyValuePair<string, string[][]>(KeyboardLayouts.Name + " shifted", KeyboardLayouts.ToggleShift()));
                KeyboardLayouts.ToggleShift();
                KeyboardLayouts.Next();
            }

            grids.Add(new KeyValuePair<string, string[][]>("symbols", KeyboardLayouts.ToggleSymbols()));
            KeyboardLayouts.ToggleSymbols();

            int letterWidth = plain[0].Length;
            int actionWidth = plain[plain.Length - 1].Length;
            foreach (KeyValuePair<string, string[][]> grid in grids)
            {
                bool shape = grid.Value.Length == plain.Length;
                for (int r = 0; shape && r < grid.Value.Length; r++)
                {
                    shape = grid.Value[r].Length == plain[r].Length;
                }

                Check(shape, grid.Key + " has the shape the cells were built for");
            }

            Check(letterWidth == 10, "letter rows are 10 wide (" + letterWidth + ")");
            Check(actionWidth == 16, "the action row is 16 wide (" + actionWidth + ")");
            Check(Array.IndexOf(plain[plain.Length - 1], KeyboardLayouts.CaretLeftKey) >= 0 &&
                  Array.IndexOf(plain[plain.Length - 1], KeyboardLayouts.CaretRightKey) >= 0,
                  "both caret arrows are on the action row");

            // 2. Moving between rows follows the screen, not the index.
            string[][] rows = KeyboardLayouts.Reset();
            int letters = 0;
            int action = rows.Length - 1;
            int shift = (actionWidth - letterWidth) / 2;

            for (int c = 0; c < letterWidth; c++)
            {
                int down = KeyboardLayouts.ColumnAcross(rows, letters, c, action);
                int back = KeyboardLayouts.ColumnAcross(rows, action, down, letters);
                Check(down == c + shift, "letter column " + c + " lands under itself on the action row (" + down + ")");
                Check(back == c, "and comes back to " + c + " (" + back + ")");
                Check(Centre(rows, action, down) == Centre(rows, letters, c),
                      "the key it landed on is directly below");
            }

            for (int c = 0; c < actionWidth; c++)
            {
                int up = KeyboardLayouts.ColumnAcross(rows, action, c, letters);
                Check(up >= 0 && up < letterWidth, "action column " + c + " lands inside the letter row (" + up + ")");
                int expected = Math.Min(letterWidth - 1, Math.Max(0, c - shift));
                Check(up == expected, "action column " + c + " goes to letter column " + expected + " (" + up + ")");
            }

            // The reporter's two moves, as he made them: from the digits down to
            // the action row, and from the action row up over the wrap to the top.
            int digits = rows.Length - 2;
            Check(KeyboardLayouts.ColumnAcross(rows, digits, 5, action) == 5 + shift,
                  "digit 5 goes down to the key beneath it, not " + shift + " keys back");
            Check(KeyboardLayouts.ColumnAcross(rows, action, 5 + shift, letters) == 5,
                  "and the key beneath it goes up over the wrap to the letter above, not " + shift + " forward");
            Check(KeyboardLayouts.ColumnAcross(rows, letters, 3, digits) == 3,
                  "rows of one width keep the column");

            // 3. The caret.
            var entry = new KeyboardEntry();
            entry.Reset("abc");
            Check(entry.Text == "abc" && entry.Caret == 3 && entry.Display == "abc|", "opens with the caret at the end");
            entry.Right();
            Check(entry.Caret == 3, "right at the end stays put");
            entry.Left();
            entry.Left();
            Check(entry.Caret == 1 && entry.Display == "a|bc", "left moves back through the text");
            entry.Insert("X");
            Check(entry.Text == "aXbc" && entry.Caret == 2, "types at the caret, not the end");
            entry.Insert(".com");
            Check(entry.Text == "aX.combc" && entry.Caret == 6, "a multi-character key moves the caret past all of it");
            Check(entry.Backspace() && entry.Text == "aX.cobc" && entry.Caret == 5, "backspace deletes before the caret");
            entry.Left();
            entry.Left();
            entry.Left();
            entry.Left();
            entry.Left();
            entry.Left();
            Check(entry.Caret == 0, "left stops at the start");
            Check(!entry.Backspace() && entry.Text == "aX.cobc", "nothing to delete at the start, and the text is kept");
            entry.Right();
            Check(entry.Backspace() && entry.Text == "X.cobc" && entry.Caret == 0, "one step in, backspace removes the first character");
            entry.Clear();
            Check(entry.Text.Length == 0 && entry.Caret == 0 && entry.Display == "|", "clear empties it and the bar stands alone");
            entry.Reset(null);
            Check(entry.Text.Length == 0 && entry.Display == "|", "opening on nothing is an empty entry");
            entry.Insert(null);
            entry.Insert(string.Empty);
            Check(entry.Text.Length == 0, "an empty insert is nothing");

            Console.WriteLine();
            if (_failures > 0)
            {
                Console.WriteLine("keyboard: " + _failures + " FAILED");
                return 1;
            }

            Console.WriteLine("keyboard: all checks passed");
            return 0;
        }
    }
}
