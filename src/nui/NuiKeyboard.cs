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
        // 14 keys on the action row (shift, sym and start joined it) at 116px each
        // overflow a 1080p panel, so the key is narrower than it was.
        private const int KeyWidth = 104;
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
                // Centred, not left-aligned: the letter rows are 10 keys and the
                // action row is 14, so left alignment leaves a hole on the right.
                int rowWidth = (KeyWidth * _rows[r].Length) + (Gap * (_rows[r].Length - 1));
                int rowLeft = (width - rowWidth) / 2;

                for (int c = 0; c < _rows[r].Length; c++)
                {
                    var key = new TextLabel
                    {
                        Position2D = new Position2D(
                            rowLeft + (c * (KeyWidth + Gap)),
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

        /// <summary>
        /// Raised when the `start` key is pressed: what was typed becomes the page
        /// the browser opens at launch. An empty entry clears it again.
        /// </summary>
        public event Action<string> StartPageSet;

        public void Open(KeyboardTarget target, string initialText)
        {
            Target = target;
            _text = initialText ?? string.Empty;
            IsVisible = true;
            _rows = KeyboardLayouts.Reset();
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
                case KeyboardLayouts.ShiftKey:
                    _rows = KeyboardLayouts.ToggleShift();
                    break;
                case KeyboardLayouts.SymbolsKey:
                    _rows = KeyboardLayouts.ToggleSymbols();
                    break;
                case KeyboardLayouts.StartPageKey:
                    // Only meaningful for an address: "make this page's search box
                    // the start page" is not a thing.
                    if (Target != KeyboardTarget.Address)
                    {
                        break;
                    }

                    string wanted = _text;
                    Close();
                    Action<string> startHandler = StartPageSet;
                    if (startHandler != null)
                    {
                        startHandler(wanted);
                    }

                    return;
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

                    // Shift applies to the next character only, as on a phone.
                    string[][] released = KeyboardLayouts.ReleaseShift();
                    if (released != null)
                    {
                        _rows = released;
                    }

                    break;
            }

            Paint();
        }

        /// <summary>
        /// Action keys read as buttons; letters stay quiet. A modifier that is on
        /// is filled green — the grid alone cannot say whether the letters showing
        /// are the shifted ones.
        /// </summary>
        private static Color FillFor(string key)
        {
            switch (key)
            {
                case "GO": return NuiTheme.Positive;
                case "close": return NuiTheme.Negative;
                case KeyboardLayouts.ShiftKey:
                    return KeyboardLayouts.Shift ? NuiTheme.Positive : NuiTheme.KeyFillAlt;
                case KeyboardLayouts.SymbolsKey:
                    return KeyboardLayouts.Symbols ? NuiTheme.Positive : NuiTheme.KeyFillAlt;
                case "back":
                case "clear":
                case "space":
                case KeyboardLayouts.CycleKey:
                case KeyboardLayouts.StartPageKey:
                case ".com": return NuiTheme.KeyFillAlt;
                default: return NuiTheme.KeyFill;
            }
        }

        /// <summary>
        /// The layout key wears the name of the layout it is showing, and the
        /// symbol key wears the page it switches *to*.
        /// </summary>
        private static string LabelFor(string key)
        {
            switch (key)
            {
                case KeyboardLayouts.CycleKey: return KeyboardLayouts.Name;
                case KeyboardLayouts.SymbolsKey: return KeyboardLayouts.Symbols ? "abc" : "sym";
                case KeyboardLayouts.ShiftKey: return KeyboardLayouts.Shift ? "SHIFT" : "shift";
                default: return key;
            }
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
