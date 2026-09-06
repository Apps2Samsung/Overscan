namespace Overscan
{
    /// <summary>
    /// Who draws the D-pad pointer. Shared because both builds now offer the
    /// choice and it means the same thing in each — see <see cref="NuiCursor"/>
    /// for what issue #78 made of it.
    /// </summary>
    internal enum CursorVisual
    {
        /// <summary>
        /// Drawn by the injected page script, as an arrow. Better looking, and only
        /// as quick as the page's main thread is willing to be.
        /// </summary>
        Dom,

        /// <summary>
        /// Drawn by the app, over the web view. A ringed dot rather than an arrow,
        /// because neither toolkit will draw a triangle, and it owes the page
        /// nothing at all.
        /// </summary>
        Native,
    }
}
