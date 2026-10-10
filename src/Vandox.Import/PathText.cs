using System.Text;

namespace Vandox.Import;

/// <summary>
/// Helpers for the path labels of files and archive entries.
/// </summary>
internal static class PathText
{
    #region Methods

    /// <summary>
    /// Cleans a slash-separated name lexically and removes leading slashes; the result is a label only.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns>The cleaned name</returns>
    internal static string CleanName(string name)
    {
        var relative = name.Length == 0 || name[0] != '/';
        var parts = new List<string>();

        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                ApplyParent(parts, relative);
            }
            else
            {
                parts.Add(part);
            }
        }

        var cleaned = string.Join('/', parts);

        return cleaned.Length == 0 ? "." : cleaned;
    }

    /// <summary>
    /// Cuts a path to <see cref="ImportLimits.MaxPathBytes"/> UTF-8 bytes at a character boundary.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns>The path, cut when needed</returns>
    internal static string Cut(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) <= ImportLimits.MaxPathBytes)
        {
            return path;
        }

        var bytes = 0;
        var builder = new StringBuilder();

        foreach (var rune in path.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;

            if (bytes > ImportLimits.MaxPathBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>
    /// Tells whether a path is longer than <see cref="ImportLimits.MaxPathBytes"/> UTF-8 bytes.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns><c>true</c> when it is too long</returns>
    internal static bool IsTooLong(string path)
    {
        return Encoding.UTF8.GetByteCount(path) > ImportLimits.MaxPathBytes;
    }

    /// <summary>
    /// Removes a trailing <c>.gz</c> (any case) from a name.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns>The name without the suffix</returns>
    internal static string TrimGzip(string name)
    {
        return name.Length > 3 && name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }

    /// <summary>
    /// Applies a <c>..</c> element: it removes the previous element, is dropped at the start of a rooted path and is kept at
    /// the start of a relative one.
    /// </summary>
    /// <param name="parts">The elements so far</param>
    /// <param name="relative">Whether the path does not start with a slash</param>
    private static void ApplyParent(List<string> parts, bool relative)
    {
        if (parts.Count > 0 && parts[^1] != "..")
        {
            parts.RemoveAt(parts.Count - 1);
        }
        else if (relative)
        {
            parts.Add("..");
        }
    }

    #endregion // Methods
}