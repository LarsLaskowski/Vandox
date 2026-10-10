namespace Vandox.Core.Configuration;

/// <summary>
/// The YAML tags the decoder accepts and the spellings of null.
/// </summary>
internal static class YamlTags
{
    #region Constants

    /// <summary>
    /// The explicit null tag.
    /// </summary>
    internal const string Null = "tag:yaml.org,2002:null";

    private const string Prefix = "tag:yaml.org,2002:";

    #endregion // Constants

    #region Fields

    private static readonly string[] _allowed = ["str", "int", "bool", "float", "null", "timestamp", "map", "seq"];

    private static readonly string[] _nullSpellings = [string.Empty, "~", "null", "Null", "NULL"];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Tells whether an explicit tag is one the decoder accepts: the core schema tags, never a custom tag, <c>!!binary</c>,
    /// <c>!!set</c> or <c>!!merge</c>.
    /// </summary>
    /// <param name="tag">The explicit tag; empty when there is none</param>
    /// <returns><c>true</c> when the tag is accepted</returns>
    internal static bool IsAllowed(string tag)
    {
        return tag.Length == 0 || (tag.StartsWith(Prefix, StringComparison.Ordinal) && _allowed.Contains(tag[Prefix.Length..]));
    }

    /// <summary>
    /// Tells whether the text is one of the null spellings of the YAML core schema.
    /// </summary>
    /// <param name="value">The text</param>
    /// <returns><c>true</c> when the text spells null</returns>
    internal static bool IsNullSpelling(string value)
    {
        return _nullSpellings.Contains(value);
    }

    #endregion // Methods
}