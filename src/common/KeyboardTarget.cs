namespace Overscan
{
    /// <summary>What a finished on-screen-keyboard entry should be used for.</summary>
    internal enum KeyboardTarget
    {
        /// <summary>Navigate to the typed text (URL or search phrase).</summary>
        Address,

        /// <summary>Type the text into whatever field the page has focused.</summary>
        PageField,
    }
}
