#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

namespace Vandox.Core.LogParsing;

/// <summary>
/// The parsers that ship with vandoxd.
/// </summary>
public static class BuiltInParsers
{
    #region Methods

    /// <summary>
    /// Creates the built-in parsers in registry order.
    /// </summary>
    /// <param name="timeZone">The IANA time zone for year-less log lines; <c>null</c> when not set</param>
    /// <returns>The parsers</returns>
    /// <exception cref="ArgumentException">The zone is unknown</exception>
    public static IReadOnlyList<ILogParser> Create(string? timeZone)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}