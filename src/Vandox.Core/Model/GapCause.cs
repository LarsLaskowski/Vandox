namespace Vandox.Core.Model;

/// <summary>
/// Names of the reasons why data is missing.
/// </summary>
public static class GapCause
{
    #region Constants

    /// <summary>
    /// The agent was not running.
    /// </summary>
    public const string AgentNotRunning = "agent_not_running";

    /// <summary>
    /// The spool dropped records.
    /// </summary>
    public const string SpoolDropped = "spool_dropped";

    /// <summary>
    /// A collector did not answer in time.
    /// </summary>
    public const string CollectorTimeout = "collector_timeout";

    /// <summary>
    /// The backend found sequence numbers missing.
    /// </summary>
    public const string SequenceMissing = "sequence_missing";

    /// <summary>
    /// The backend received no data.
    /// </summary>
    public const string NoData = "no_data";

    /// <summary>
    /// The cause is unknown.
    /// </summary>
    public const string Unknown = "unknown";

    #endregion // Constants
}