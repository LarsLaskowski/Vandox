namespace Vandox.Core.LogParsing;

/// <summary>
/// Tells how well a parser matches a file; the registry picks the highest.
/// </summary>
public enum Confidence
{
    /// <summary>
    /// The parser cannot read the file.
    /// </summary>
    NoMatch = 0,

    /// <summary>
    /// Only the name fits, or the content is not specific.
    /// </summary>
    MatchName = 1,

    /// <summary>
    /// The content carries the signature of the format.
    /// </summary>
    MatchContent = 2
}