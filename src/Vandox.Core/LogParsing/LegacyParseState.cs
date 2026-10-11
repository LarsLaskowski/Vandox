namespace Vandox.Core.LogParsing;

/// <summary>
/// What a parse of the legacy monitoring log remembers from one snapshot to the next.
/// </summary>
internal sealed class LegacyParseState
{
    #region Properties

    /// <summary>
    /// Gets the last value of each vmstat counter.
    /// </summary>
    internal Dictionary<string, LegacyCounter> Counters { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets a value indicating whether a header of this parse has resolved.
    /// </summary>
    internal bool Resolved { get; set; }

    #endregion // Properties
}