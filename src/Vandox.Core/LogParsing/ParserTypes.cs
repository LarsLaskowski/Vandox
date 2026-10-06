using System.Text.RegularExpressions;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The rule for the type name of a log parser.
/// </summary>
public static partial class ParserTypes
{
    #region Methods

    /// <summary>
    /// Tells whether a text is a valid parser type: a lower-case letter, then up to 63 lower-case letters, digits, dots,
    /// underscores or hyphens.
    /// </summary>
    /// <param name="type">The text</param>
    /// <returns><c>true</c> when the text is a valid parser type</returns>
    public static bool IsValid(string type)
    {
        return TypePattern().IsMatch(type);
    }

    /// <summary>
    /// Creates the pattern of a parser type.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[a-z][a-z0-9._\-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    #endregion // Methods
}