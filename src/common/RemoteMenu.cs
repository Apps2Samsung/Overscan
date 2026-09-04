using System;

namespace Overscan
{
    /// <summary>
    /// The list of everything the browser can do, and which entry is currently
    /// picked out — the part of an on-screen menu that has nothing to do with how
    /// it is drawn. Both builds own the same list and the same selection rules;
    /// only the drawing differs, which is why this is here and not in either app.
    /// </summary>
    /// <remarks>
    /// This exists because of issue #27. Every function in this browser used to
    /// sit behind a number key, and a slim Samsung remote has no number keys —
    /// so on those remotes the app was a cursor and nothing else. The menu is
    /// reachable with the D-pad alone, which every remote has, and it lists the
    /// number-key shortcut beside each entry so the menu also teaches the faster
    /// way for anyone holding a remote that has them.
    /// </remarks>
    internal sealed class RemoteMenu
    {
        /// <summary>One row of the menu.</summary>
        internal readonly struct Item
        {
            public Item(string id, string label, string shortcut)
            {
                Id = id;
                Label = label;
                Shortcut = shortcut;
            }

            /// <summary>What the app switches on when this row is chosen.</summary>
            public string Id { get; }

            /// <summary>What the row reads on screen.</summary>
            public string Label { get; }

            /// <summary>The number key that does the same thing, or empty.</summary>
            public string Shortcut { get; }
        }

        // Action ids. Strings rather than an enum so each build can offer its own
        // subset without either one carrying names for things it cannot do.
        public const string ActionAddress = "address";
        public const string ActionHome = "home";
        public const string ActionBookmark = "bookmark";
        public const string ActionIdentity = "identity";
        public const string ActionTypeInField = "typefield";
        public const string ActionKeysToPage = "keystopage";
        public const string ActionFitPage = "fitpage";
        public const string ActionImages = "images";
        public const string ActionAdBlock = "adblock";
        public const string ActionVideoPath = "videopath";
        public const string ActionPointer = "pointer";
        public const string ActionDiagnostics = "diagnostics";
        public const string ActionHints = "hints";
        public const string ActionQuit = "quit";

        private readonly Item[] _items;
        private int _index;

        public RemoteMenu(Item[] items)
        {
            if (items == null || items.Length == 0)
            {
                throw new ArgumentException("a menu needs at least one item", nameof(items));
            }

            _items = items;
        }

        public bool Visible { get; private set; }

        public int Count
        {
            get { return _items.Length; }
        }

        public int SelectedIndex
        {
            get { return _index; }
        }

        public Item Selected
        {
            get { return _items[_index]; }
        }

        public Item ItemAt(int i)
        {
            return _items[i];
        }

        /// <summary>
        /// Opens the menu with the first entry picked out. The selection is not
        /// remembered between openings: a menu that reopens where you left it is
        /// quicker for the second use of one entry and confusing for every other,
        /// and at TV distance the confusing case is the common one.
        /// </summary>
        public void Open()
        {
            _index = 0;
            Visible = true;
        }

        public void Close()
        {
            Visible = false;
        }

        /// <summary>
        /// Moves the selection, wrapping at both ends — a D-pad has no scrollbar
        /// to tell you the list has run out, so wrapping is what stops a press
        /// from feeling broken.
        /// </summary>
        public void Move(int delta)
        {
            if (!Visible)
            {
                return;
            }

            _index = ((_index + delta) % _items.Length + _items.Length) % _items.Length;
        }
    }
}
