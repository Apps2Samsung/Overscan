namespace Overscan
{
    /// <summary>What a finished on-screen-keyboard entry should be used for.</summary>
    internal enum KeyboardTarget
    {
        /// <summary>Navigate to the typed text (URL or search phrase).</summary>
        Address,

        /// <summary>Type the text into whatever field the page has focused.</summary>
        PageField,

        /// <summary>
        /// Keep the typed address as a favourite without going to it (issue #80).
        /// The reporter wanted `instagram.com/reel` in his tiles, and that address
        /// redirects to one particular reel the moment it is opened — so the only
        /// favourite he could make was of the reel, which then played the same clip
        /// for ever. An address you cannot land on has no other way in.
        /// </summary>
        Favourite,
    }
}
