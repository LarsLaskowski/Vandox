namespace Vandox.Core.LogParsing;

/// <summary>
/// Picks the parser for a file: the one with the highest confidence; on equal confidence the earlier one wins.
/// </summary>
public sealed class ParserRegistry
{
    #region Fields

    private readonly ILogParser[] _parsers;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ParserRegistry"/> class.
    /// </summary>
    /// <param name="parsers">The parsers in priority order</param>
    /// <exception cref="ArgumentException">A parser is <c>null</c>, has an invalid type, or its type is registered twice</exception>
    public ParserRegistry(IEnumerable<ILogParser?> parsers)
    {
        var list = new List<ILogParser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parser in parsers)
        {
            list.Add(Check(parser, list.Count, seen));
        }

        _parsers = [.. list];
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns the parser types in registration order.
    /// </summary>
    /// <returns>The types</returns>
    public IReadOnlyList<string> GetTypes()
    {
        return _parsers.Select(parser => parser.Type).ToList();
    }

    /// <summary>
    /// Returns the parser with the highest confidence for a file.
    /// </summary>
    /// <param name="file">The file</param>
    /// <param name="head">The head of the decompressed content</param>
    /// <returns>The parser and its confidence, or <c>null</c> and <see cref="Confidence.NoMatch"/></returns>
    public (ILogParser? Parser, Confidence Confidence) Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        ILogParser? best = null;
        var bestConfidence = Confidence.NoMatch;

        foreach (var parser in _parsers)
        {
            var confidence = parser.Detect(file, head);

            if (confidence > bestConfidence)
            {
                best = parser;
                bestConfidence = confidence;
            }
        }

        return (best, bestConfidence);
    }

    /// <summary>
    /// Checks a parser of the list.
    /// </summary>
    /// <param name="parser">The parser</param>
    /// <param name="index">Its position</param>
    /// <param name="seen">The types registered so far</param>
    /// <returns>The parser</returns>
    private static ILogParser Check(ILogParser? parser, int index, HashSet<string> seen)
    {
        if (parser is null)
        {
            throw new ArgumentException($"logparse: invalid parser: parser {index} is null", nameof(parser));
        }

        if (ParserTypes.IsValid(parser.Type))
        {
            if (seen.Add(parser.Type))
            {
                return parser;
            }

            throw new ArgumentException($"logparse: invalid parser: type \"{parser.Type}\" registered twice", nameof(parser));
        }

        throw new ArgumentException($"logparse: invalid parser: type {Model.FieldError.QuoteName(parser.Type)} must match ^[a-z][a-z0-9._-]{{0,63}}$", nameof(parser));
    }

    #endregion // Methods
}