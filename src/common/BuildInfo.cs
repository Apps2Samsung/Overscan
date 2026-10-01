using System.Reflection;

namespace Overscan
{
    /// <summary>
    /// Which release a diagnostics report came from. Until now neither the `:8081`
    /// page nor the key-3 screen said, and on issues #95 and #105 the build a
    /// reporter ran had to be read off release timestamps.
    ///
    /// CI stamps the tag into the assembly (`-p:OverscanBuildTag=build-&lt;sha7&gt;`,
    /// the same name the release job gives the release) through
    /// <c>Directory.Build.props</c>; a local build carries its default instead.
    /// </summary>
    internal static class BuildInfo
    {
        public static string Tag { get; } = Read();

        private static string Read()
        {
            try
            {
                object[] found = typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
                foreach (object attribute in found)
                {
                    var metadata = (AssemblyMetadataAttribute)attribute;
                    if (metadata.Key == "OverscanBuild" && !string.IsNullOrEmpty(metadata.Value))
                    {
                        return metadata.Value;
                    }
                }
            }
            catch
            {
            }

            return "(not stamped)";
        }
    }
}
