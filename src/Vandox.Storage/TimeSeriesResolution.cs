namespace Vandox.Storage;

/// <summary>
/// The resolution of a time-series result.
/// </summary>
public enum TimeSeriesResolution
{
    /// <summary>
    /// Raw samples.
    /// </summary>
    Raw,

    /// <summary>
    /// Rollup steps.
    /// </summary>
    Step
}