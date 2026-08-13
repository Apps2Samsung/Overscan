using System;
using ElmSharp;

namespace Overscan
{
    /// <summary>
    /// The NuiKeyboard grid, rebuilt on ElmSharp.
    ///
    /// This replaces the address-bar <see cref="Entry"/> outright. On a real TV the
    /// Entry takes focus at startup and the platform IME comes up over the page,
    /// swallowing every remote key — the cursor could not be moved at all. A grid we
    /// draw and drive ourselves never involves the IME, and it can type into page
    /// fields too, which the IME cannot.
    ///
    /// Keys are Rectangle + Label pairs positioned absolutely and raised above the
    /// conformant, because there is no focus handling to fight this way.
    /// </summary>
    internal sealed class ElmKeyboard
    {
        private const int KeyWidth = 116;
        private const int KeyHeight = 74;
        private const int Gap = 8;
        private const int EntryHeight = 96;

        private static readonly string[][] Rows =
        {
            new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" },
            new[] { "k", "l", "m", "n", "o", "p", "q", "r", "s", "t" },
            new[] { "u", "v", "w", "x", "y", "z", "-", "_", "?", "=" },
            new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
            new[] { ".", "/", ":", "&", "space", ".com", "back", "clear", "GO", "close" },
        };

        private readonly Rectangle _panel;
        private readonly Label _entry;
        private readonly Rectangle[][] _cells;
        private readonly Label[][] _labels;

        private int _row;
        private int _column;
        private string _text = string.Empty;

        public ElmKeyboard(Window window)
        {
            int width = (KeyWidth * 10) + (Gap * 11);
            int height = (KeyHeight * Rows.Length) + (Gap * (Rows.Length + 1)) + EntryHeight;
            Size screen = window.ScreenSize;
            int left = Math.Max(0, (screen.Width - width) / 2);
            int top = Math.Max(0, screen.Height - height - 48);

            _panel = new Rectangle(window)
            {
                Color = Color.FromRgba(12, 12, 18, 245),
                Geometry = new Rect(left, top, width, height),
            };

            _entry = new Label(window)
            {
                Geometry = new Rect(left + (Gap * 2), top + (Gap * 2), width - (Gap * 4), EntryHeight - (Gap * 2)),
            };

            _cells = new Rectangle[Rows.Length][];
            _labels = new Label[Rows.Length][];

            for (int r = 0; r < Rows.Length; r++)
            {
                _cells[r] = new Rectangle[Rows[r].Length];
                _labels[r] = new Label[Rows[r].Length];

                for (int c = 0; c < Rows[r].Length; c++)
                {
                    var cell = new Rect(
                        left + Gap + (c * (KeyWidth + Gap)),
                        top + EntryHeight + (r * (KeyHeight + Gap)),
                        KeyWidth,
                        KeyHeight);

                    _cells[r][c] = new Rectangle(window) { Geometry = cell };
                    _labels[r][c] = new Label(window)
                    {
                        Geometry = new Rect(cell.X + 8, cell.Y + 20, cell.Width - 16, cell.Height - 24),
                        Text = Markup(Rows[r][c], false),
                    };
                }
            }
        }

        public bool IsVisible { get; private set; }

        public KeyboardTarget Target { get; private set; }

        /// <summary>Raised with the finished text when GO is pressed.</summary>
        public event Action<string, KeyboardTarget> Committed;

        public void Open(KeyboardTarget target, string initialText)
        {
            Target = target;
            _text = initialText ?? string.Empty;
            IsVisible = true;

            _panel.Show();
            _panel.RaiseTop();
            _entry.Show();
            _entry.RaiseTop();

            foreach (Rectangle[] row in _cells)
            {
                foreach (Rectangle cell in row)
                {
                    cell.Show();
                    cell.RaiseTop();
                }
            }

            foreach (Label[] row in _labels)
            {
                foreach (Label label in row)
                {
                    label.Show();
                    label.RaiseTop();
                }
            }

            Paint();
            DiagLog.Add("keyboard opened for " + target);
        }

        public void Close()
        {
            IsVisible = false;
            _panel.Hide();
            _entry.Hide();

            foreach (Rectangle[] row in _cells)
            {
                foreach (Rectangle cell in row)
                {
                    cell.Hide();
                }
            }

            foreach (Label[] row in _labels)
            {
                foreach (Label label in row)
                {
                    label.Hide();
                }
            }

            DiagLog.Add("keyboard closed");
        }

        /// <summary>Handles a remote key. Returns false if the key is not ours.</summary>
        public bool HandleKey(string key)
        {
            switch (key)
            {
                case RemoteKeys.Left:
                    _column = _column > 0 ? _column - 1 : Rows[_row].Length - 1;
                    break;
                case RemoteKeys.Right:
                    _column = (_column + 1) % Rows[_row].Length;
                    break;
                case RemoteKeys.Up:
                    _row = _row > 0 ? _row - 1 : Rows.Length - 1;
                    _column = Math.Min(_column, Rows[_row].Length - 1);
                    break;
                case RemoteKeys.Down:
                    _row = (_row + 1) % Rows.Length;
                    _column = Math.Min(_column, Rows[_row].Length - 1);
                    break;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    Press(Rows[_row][_column]);
                    return true;

                case RemoteKeys.Back:
                    if (_text.Length > 0)
                    {
                        _text = _text.Substring(0, _text.Length - 1);
                    }
                    else
                    {
                        Close();
                        return true;
                    }

                    break;

                default:
                    return false;
            }

            Paint();
            return true;
        }

        private void Press(string label)
        {
            switch (label)
            {
                case "space":
                    _text += " ";
                    break;
                case ".com":
                    _text += ".com";
                    break;
                case "back":
                    if (_text.Length > 0)
                    {
                        _text = _text.Substring(0, _text.Length - 1);
                    }

                    break;
                case "clear":
                    _text = string.Empty;
                    break;
                case "close":
                    Close();
                    return;
                case "GO":
                    string committed = _text;
                    Close();
                    Action<string, KeyboardTarget> handler = Committed;
                    if (handler != null)
                    {
                        handler(committed, Target);
                    }

                    return;
                default:
                    _text += label;
                    break;
            }

            Paint();
        }

        private void Paint()
        {
            _entry.Text = Markup(
                (Target == KeyboardTarget.Address ? "url> " : "page> ") +
                (_text.Length == 0 ? "_" : _text), false);

            for (int r = 0; r < _cells.Length; r++)
            {
                for (int c = 0; c < _cells[r].Length; c++)
                {
                    bool selected = r == _row && c == _column;
                    _cells[r][c].Color = selected
                        ? Color.FromRgba(72, 148, 255, 255)
                        : Color.FromRgba(40, 40, 48, 255);
                    _labels[r][c].Text = Markup(Rows[r][c], selected);
                }
            }
        }

        private static string Markup(string text, bool selected)
        {
            string escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return (selected ? "<color=#ffffff><b>" : "<color=#e8e8ec>") + escaped +
                   (selected ? "</b></color>" : "</color>");
        }
    }
}
