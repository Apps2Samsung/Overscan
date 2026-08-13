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

        private static readonly string[][] Rows =
        {
            new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j" },
            new[] { "k", "l", "m", "n", "o", "p", "q", "r", "s", "t" },
            new[] { "u", "v", "w", "x", "y", "z", "-", "_", "?", "=" },
            new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" },
            new[] { ".", "/", ":", "&", "space", ".com", "back", "clear", "GO", "close" },
        };

        private readonly Window _window;
        private readonly View _root;
        private readonly TextLabel _entry;
        private readonly TextLabel[][] _keys;

        private int _row;
        private int _column;
        private string _text = string.Empty;

        public NuiKeyboard(Window window)
        {
            _window = window;

            int width = (KeyWidth * 10) + (Gap * 11);
            int height = (KeyHeight * Rows.Length) + (Gap * (Rows.Length + 1)) + 96;
            int left = (window.WindowSize.Width - width) / 2;
            int top = window.WindowSize.Height - height - 48;

            _root = new View
            {
                Position2D = new Position2D(left, top),
                Size2D = new Size2D(width, height),
                BackgroundColor = new Color(0.05f, 0.05f, 0.07f, 0.96f),
            };

            _entry = new TextLabel
            {
                Position2D = new Position2D(Gap * 2, Gap * 2),
                Size2D = new Size2D(width - (Gap * 4), 64),
                PointSize = 14,
                TextColor = Color.White,
                Text = string.Empty,
            };
            _root.Add(_entry);

            _keys = new TextLabel[Rows.Length][];
            for (int r = 0; r < Rows.Length; r++)
            {
                _keys[r] = new TextLabel[Rows[r].Length];
                for (int c = 0; c < Rows[r].Length; c++)
                {
                    var key = new TextLabel
                    {
                        Position2D = new Position2D(
                            Gap + (c * (KeyWidth + Gap)),
                            96 + (r * (KeyHeight + Gap))),
                        Size2D = new Size2D(KeyWidth, KeyHeight),
                        PointSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Text = Rows[r][c],
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
            _entry.Text = (Target == KeyboardTarget.Address ? "url> " : "page> ") +
                          (_text.Length == 0 ? "_" : _text);

            for (int r = 0; r < _keys.Length; r++)
            {
                for (int c = 0; c < _keys[r].Length; c++)
                {
                    bool selected = r == _row && c == _column;
                    _keys[r][c].BackgroundColor = selected
                        ? new Color(0.28f, 0.58f, 1f, 1f)
                        : new Color(0.16f, 0.16f, 0.19f, 1f);
                    _keys[r][c].TextColor = Color.White;
                }
            }
        }
    }
}
