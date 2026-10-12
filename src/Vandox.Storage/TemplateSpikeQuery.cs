namespace Vandox.Storage;

/// <summary>
/// Selects the template hours in the range whose count is above the usual level of the template.
/// </summary>
public sealed class TemplateSpikeQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the source; empty for every source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the range; required.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range; required, after <see cref="From"/>, at most <see cref="StorageLimits.MaxSpikeRangeHours"/> hours.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets the hours before an hour that form its baseline: 1 to <see cref="StorageLimits.MaxBaselineHours"/>.
    /// </summary>
    public int BaselineHours { get; set; } = StorageLimits.DefaultBaselineHours;

    /// <summary>
    /// Gets or sets the fewest baseline hours with lines of the source that a result needs: 1 to <see cref="BaselineHours"/>.
    /// </summary>
    public int MinBaselineHours { get; set; } = StorageLimits.DefaultMinBaselineHours;

    /// <summary>
    /// Gets or sets the factor the count must exceed the usual level by: 1 to <see cref="StorageLimits.MaxSpikeFactor"/>.
    /// </summary>
    public double Factor { get; set; } = StorageLimits.DefaultSpikeFactor;

    /// <summary>
    /// Gets or sets the fewest lines an hour needs to be reported; at least 1.
    /// </summary>
    public long MinCount { get; set; } = StorageLimits.DefaultSpikeMinCount;

    /// <summary>
    /// Gets or sets the most spikes returned: 1 to <see cref="StorageLimits.MaxQueryLimit"/>.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}