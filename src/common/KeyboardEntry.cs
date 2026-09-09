namespace Overscan
{
    /// <summary>
    /// The line of text the on-screen keyboard is building, and where in it the
    /// next character lands.
    ///
    /// Shared by the ElmSharp and NUI keyboards so both edit the same way. Until
    /// issue #92 the entry was append-only: a typo three characters back in an
    /// address meant deleting everything after it and typing it again, on a
    /// D-pad, which is what the reporter was asking to be spared when he asked for
    /// arrow keys. The caret is an index into <see cref="Text"/>; everything the
    /// keyboards do to the text goes through here so the two cannot drift apart.
    /// </summary>
    internal sealed class KeyboardEntry
    {
        private string _text = string.Empty;
        private int _caret;

        /// <summary>What has been typed so far.</summary>
        public string Text
        {
            get { return _text; }
        }

        /// <summary>Where the next character goes: 0 is before the first one.</summary>
        public int Caret
        {
            get { return _caret; }
        }

        /// <summary>
        /// The text with the caret drawn into it as a bar, for the entry line. An
        /// empty entry is a bar on its own, as it always was.
        /// </summary>
        public string Display
        {
            get { return _text.Substring(0, _caret) + "|" + _text.Substring(_caret); }
        }

        /// <summary>
        /// Starts over with <paramref name="text"/> and the caret at its end: the
        /// keyboard opens on an address to add to it, or on nothing at all.
        /// </summary>
        public void Reset(string text)
        {
            _text = text ?? string.Empty;
            _caret = _text.Length;
        }

        /// <summary>Puts <paramref name="value"/> in at the caret and moves past it.</summary>
        public void Insert(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            _text = _text.Substring(0, _caret) + value + _text.Substring(_caret);
            _caret += value.Length;
        }

        /// <summary>
        /// Removes the character before the caret. False when there was none —
        /// the keyboards close on a remote Back key that has nothing left to delete.
        /// </summary>
        public bool Backspace()
        {
            if (_caret == 0)
            {
                return false;
            }

            _text = _text.Substring(0, _caret - 1) + _text.Substring(_caret);
            _caret--;
            return true;
        }

        public void Clear()
        {
            _text = string.Empty;
            _caret = 0;
        }

        /// <summary>One character towards the start; stays put at the start.</summary>
        public void Left()
        {
            if (_caret > 0)
            {
                _caret--;
            }
        }

        /// <summary>One character towards the end; stays put at the end.</summary>
        public void Right()
        {
            if (_caret < _text.Length)
            {
                _caret++;
            }
        }
    }
}
