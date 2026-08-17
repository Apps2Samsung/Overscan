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

        private readonly Rectangle _panel;
        private readonly Rectangle _panelEdge;
        private readonly Label _entry;
        private readonly Rectangle[][] _cells;
        private readonly Rectangle[][] _edges;
        private readonly Label[][] _labels;

        /// <summary>
        /// The current grid. Only the labels change when the layout is switched —
        /// every layout has the same shape, so the cells below stay valid.
        /// </summary>
        private string[][] _rows = KeyboardLayouts.Rows;

        private int _row;
        private int _column;
        private string _text = string.Empty;

        public ElmKeyboard(Window window)
        {
            int columns = 0;
            foreach (string[] row in _rows)
            {
                columns = Math.Max(columns, row.Length);
            }

            int width = (KeyWidth * columns) + (Gap * (columns + 1));
            int height = (KeyHeight * _rows.Length) + (Gap * (_rows.Length + 1)) + EntryHeight;
            Size screen = window.ScreenSize;
            int left = Math.Max(0, (screen.Width - width) / 2);
            int top = Math.Max(0, screen.Height - height - 48);

            _panelEdge = new Rectangle(window)
            {
                Color = Theme.Edge,
                Geometry = new Rect(left - 2, top - 2, width + 4, height + 4),
            };

            _panel = new Rectangle(window)
            {
                Color = Theme.PanelDeep,
                Geometry = new Rect(left, top, width, height),
            };

            _entry = new Label(window)
            {
                Geometry = new Rect(left + (Gap * 2), top + (Gap * 2), width - (Gap * 4), EntryHeight - (Gap * 2)),
            };

            _cells = new Rectangle[_rows.Length][];
            _edges = new Rectangle[_rows.Length][];
            _labels = new Label[_rows.Length][];

            for (int r = 0; r < _rows.Length; r++)
            {
                _cells[r] = new Rectangle[_rows[r].Length];
                _edges[r] = new Rectangle[_rows[r].Length];
                _labels[r] = new Label[_rows[r].Length];

                for (int c = 0; c < _rows[r].Length; c++)
                {
                    var cell = new Rect(
                        left + Gap + (c * (KeyWidth + Gap)),
                        top + EntryHeight + (r * (KeyHeight + Gap)),
                        KeyWidth,
                        KeyHeight);

                    _edges[r][c] = new Rectangle(window)
                    {
                        Color = Theme.Edge,
                        Geometry = cell,
                    };
                    _cells[r][c] = new Rectangle(window)
                    {
                        Geometry = new Rect(cell.X + 2, cell.Y + 2, cell.Width - 4, cell.Height - 4),
                    };
                    _labels[r][c] = new Label(window)
                    {
                        Geometry = new Rect(cell.X, cell.Y + 18, cell.Width, cell.Height - 20),
                        Text = Label(_rows[r][c], false),
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

            _panelEdge.Show();
            _panelEdge.RaiseTop();
            _panel.Show();
            _panel.RaiseTop();
            _entry.Show();
            _entry.RaiseTop();

            foreach (Rectangle[] row in _edges)
            {
                foreach (Rectangle edge in row)
                {
                    edge.Show();
                    edge.RaiseTop();
                }
            }

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
            _panelEdge.Hide();
            _entry.Hide();

            foreach (Rectangle[] row in _cells)
            {
                foreach (Rectangle cell in row)
                {
                    cell.Hide();
                }
            }

            foreach (Rectangle[] row in _edges)
            {
                foreach (Rectangle edge in row)
                {
                    edge.Hide();
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
                    _column = _column > 0 ? _column - 1 : _rows[_row].Length - 1;
                    break;
                case RemoteKeys.Right:
                    _column = (_column + 1) % _rows[_row].Length;
                    break;
                case RemoteKeys.Up:
                    _row = _row > 0 ? _row - 1 : _rows.Length - 1;
                    _column = Math.Min(_column, _rows[_row].Length - 1);
                    break;
                case RemoteKeys.Down:
                    _row = (_row + 1) % _rows.Length;
                    _column = Math.Min(_column, _rows[_row].Length - 1);
                    break;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    Press(_rows[_row][_column]);
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
                case KeyboardLayouts.CycleKey:
                    _rows = KeyboardLayouts.Next();
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
            string prompt = Target == KeyboardTarget.Address ? "Go to" : "Type into page";
            _entry.Text =
                Theme.Text(prompt + "   ", 24, Theme.Accent, true) +
                Theme.Text(_text.Length == 0 ? "|" : _text + "|", 34, Theme.Ink, true);

            for (int r = 0; r < _cells.Length; r++)
            {
                for (int c = 0; c < _cells[r].Length; c++)
                {
                    bool selected = r == _row && c == _column;
                    _cells[r][c].Color = selected ? Theme.Accent : FillFor(_rows[r][c]);
                    _labels[r][c].Text = Label(_rows[r][c], selected);
                    _edges[r][c].Color = selected ? Theme.Accent : Theme.Edge;
                }
            }
        }

        /// <summary>Action keys read as buttons; letters stay quiet.</summary>
        private static Color FillFor(string key)
        {
            switch (key)
            {
                case "GO": return Theme.Positive;
                case "close": return Theme.Negative;
                case "back":
                case "clear":
                case "space":
                case KeyboardLayouts.CycleKey:
                case ".com": return Theme.KeyFillAlt;
                default: return Theme.KeyFill;
            }
        }

        /// <summary>The layout key wears the name of the layout it is showing.</summary>
        private static string Label(string key, bool selected)
        {
            string text = key == KeyboardLayouts.CycleKey ? KeyboardLayouts.Name : key;
            bool wide = text.Length > 1;
            return Theme.Text(text, wide ? 22 : 30, selected ? Theme.Ink : Theme.InkMuted,
                              selected || wide, "center");
        }

    }
}
