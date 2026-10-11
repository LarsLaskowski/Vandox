namespace Vandox.Core.LogParsing;

/// <summary>
/// Matches configured log paths against file names.
/// </summary>
internal static class LogPath
{
    #region Constants

    private const int DateDigits = 8;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Tells whether a file is the configured log file or a rotation of it: the parent directory of the file equals the parent
    /// directory of the configured path and the base name equals the configured base name, alone or followed by <c>.</c> and
    /// digits or by <c>-</c> and eight digits. The paths are compared as text; nothing is opened or resolved.
    /// </summary>
    /// <param name="configuredPath">The configured path</param>
    /// <param name="fileName">The display path of the file</param>
    /// <returns><c>true</c> when the file matches</returns>
    internal static bool Matches(string configuredPath, string fileName)
    {
        return Split(configuredPath, out var configuredParent, out var configuredBase)
               && Split(fileName, out var parent, out var baseName)
               && parent.SequenceEqual(configuredParent)
               && HasRotationSuffix(baseName, configuredBase);
    }

    /// <summary>
    /// Splits a path into the name of its parent directory and its base name.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="parent">The last directory component</param>
    /// <param name="baseName">The last component</param>
    /// <returns><c>true</c> when the path has a non-empty directory component and a non-empty base name</returns>
    private static bool Split(ReadOnlySpan<char> path, out ReadOnlySpan<char> parent, out ReadOnlySpan<char> baseName)
    {
        var last = path.LastIndexOf('/');

        parent = default;
        baseName = default;

        if (last >= 0)
        {
            var directory = path[..last];

            parent = directory[(directory.LastIndexOf('/') + 1)..];
            baseName = path[(last + 1)..];
        }

        return parent.Length > 0 && baseName.Length > 0;
    }

    /// <summary>
    /// Tells whether a base name is the configured one, alone or with a rotation suffix.
    /// </summary>
    /// <param name="baseName">The base name of the file</param>
    /// <param name="configuredBase">The configured base name</param>
    /// <returns><c>true</c> when the name matches</returns>
    private static bool HasRotationSuffix(ReadOnlySpan<char> baseName, ReadOnlySpan<char> configuredBase)
    {
        if (baseName.StartsWith(configuredBase, StringComparison.Ordinal))
        {
            var suffix = baseName[configuredBase.Length..];

            return suffix.Length == 0
                   || (suffix.Length > 1 && suffix[0] == '.' && IsDigits(suffix[1..]))
                   || (suffix.Length == DateDigits + 1 && suffix[0] == '-' && IsDigits(suffix[1..]));
        }

        return false;
    }

    /// <summary>
    /// Tells whether a text consists of ASCII digits only.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> for digits only</returns>
    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        return text.IndexOfAnyExceptInRange('0', '9') < 0;
    }

    #endregion // Methods
}