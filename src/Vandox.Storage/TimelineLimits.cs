namespace Vandox.Storage;

/// <summary>
/// Limits and windows of the live or backfilled classification.
/// </summary>
public static class TimelineLimits
{
    #region Constants

    /// <summary>
    /// The largest send lag in seconds a self sample may report to be counted for the clock offset.
    /// </summary>
    public const double MaxCountedSendLagSeconds = 60;

    /// <summary>
    /// The most clock sample rows kept per agent and sort.
    /// </summary>
    public const int MaxClockSamples = 64;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The allowance of run-timed records: how far their corrected capture time may lie from the receive time.
    /// </summary>
    public static readonly TimeSpan RunAllowance = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The allowance of log lines and kernel events.
    /// </summary>
    public static readonly TimeSpan LogAllowance = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The span between two samples above which a time gap exists.
    /// </summary>
    public static readonly TimeSpan TimeGapSpan = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The time without a filling batch after which open sequence gaps become final.
    /// </summary>
    public static readonly TimeSpan FinalWait = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The window of clock samples the offset estimate looks at.
    /// </summary>
    public static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The least spacing, in capture and in receive time, between a counted sample and the previous one that confirms it.
    /// </summary>
    public static readonly TimeSpan ConfirmSpacing = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The width of one clock sample bucket of receive time.
    /// </summary>
    public static readonly TimeSpan SampleBucket = TimeSpan.FromSeconds(10);

    #endregion // Fields
}