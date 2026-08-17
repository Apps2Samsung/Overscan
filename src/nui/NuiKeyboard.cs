using System;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// On-screen keyboard driven by the D-pad.
    ///
    /// Deliberately not the platform IME: a TV set's on-screen keyboard is only
    /// raised for an ElmSharp/NUI text widget, and whether it appears at all
    /// varies by set. A grid we draw ourselves works the same everywhere, and it
    /// can type into a *page* field as well as our own address bar — which is what
    /// makes in-page search boxes usable.
    /// </summary>
    internal sealed class NuiKeyboard
    {
        private const int KeyWidth = 116;
        private const int KeyHeight = 74;
        private const int Gap = 8;

        private readonly Window _window;
        private readonly View _root;
        private readonly TextLabel _entry;
        private readonly TextLabel[][] _keys;

        /// <summary>
        /// The current grid. Only the labels change when the layout is switched —
        /// every layout has the same shape, so the keys below stay valid.
        /// </summary>
        private string[][] _rows = KeyboardLayouts.Rows;

        private int _row;
        private int _column;
        private string _text = string.Empty;

        public NuiKeyboard(Window window)
        {
            _window = window;

            int columns = 0;
            foreach (string[] row in _rows)
            {
                columns = Math.Max(columns, row.Length);
            }

            int width = (KeyWidth * columns) + (Gap * (columns + 1));
            int height = (KeyHeight * _rows.Length) + (Gap * (_rows.Length + 1)) + 96;
            int left = (window.WindowSize.Width - width) / 2;
            int top = window.WindowSize.Height - height - 48;

            _root = new View
            {
                Position2D = new Position2D(left, top),
                Size2D = new Size2D(width, height),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            _entry = new TextLabel
            {
                Position2D = new Position2D(Gap * 2, Gap * 2),
                Size2D = new Size2D(width - (Gap * 4), 64),
                PointSize = 16,
                TextColor = NuiTheme.Ink,
                Text = string.Empty,
            };
            _root.Add(_entry);

            _keys = new TextLabel[_rows.Length][];
            for (int r = 0; r < _rows.Length; r++)
            {
                _keys[r] = new TextLabel[_rows[r].Length];
                for (int c = 0; c < _rows[r].Length; c++)
                {
                    var key = new TextLabel
                    {
                        Position2D = new Position2D(
                            Gap + (c * (KeyWidth + Gap)),
                            96 + (r * (KeyHeight + Gap))),
                        Size2D = new Size2D(KeyWidth, KeyHeight),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        CornerRadius = 8f,
                    };
                    _keys[r][c] = key;
                    _root.Add(key);
                }
            }

            _root.Hide();
            window.Add(_root);
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
            _root.Show();
            _root.RaiseToTop();
            Paint();
            DiagLog.Add("keyboard opened for " + target);
        }

        public void Close()
        {
            IsVisible = false;
            _root.Hide();
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

        /// <summary>Action keys read as buttons; letters stay quiet.</summary>
        private static Color FillFor(string key)
        {
            switch (key)
            {
                case "GO": return NuiTheme.Positive;
                case "close": return NuiTheme.Negative;
                case "back":
                case "clear":
                case "space":
                case KeyboardLayouts.CycleKey:
                case ".com": return NuiTheme.KeyFillAlt;
                default: return NuiTheme.KeyFill;
            }
        }

        /// <summary>The layout key wears the name of the layout it is showing.</summary>
        private static string LabelFor(string key)
        {
            return key == KeyboardLayouts.CycleKey ? KeyboardLayouts.Name : key;
        }

        private void Paint()
        {
            _entry.Text = (Target == KeyboardTarget.Address ? "Go to   " : "Type into page   ") +
                          (_text.Length == 0 ? "|" : _text + "|");

            for (int r = 0; r < _keys.Length; r++)
            {
                for (int c = 0; c < _keys[r].Length; c++)
                {
                    bool selected = r == _row && c == _column;
                    string label = LabelFor(_rows[r][c]);
                    _keys[r][c].Text = label;
                    _keys[r][c].PointSize = label.Length > 1 ? 10 : 14;
                    _keys[r][c].BackgroundColor = selected ? NuiTheme.Accent : FillFor(_rows[r][c]);
                    _keys[r][c].TextColor = selected ? NuiTheme.Ink : NuiTheme.InkMuted;
                }
            }
        }
    }
}
