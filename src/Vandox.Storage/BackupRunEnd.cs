namespace Vandox.Storage;

/// <summary>
/// How a backup run ended.
/// </summary>
public enum BackupRunEnd
{
    /// <summary>
    /// The run ended.
    /// </summary>
    Ended,

    /// <summary>
    /// The run is still running.
    /// </summary>
    Running,

    /// <summary>
    /// The end is unknown.
    /// </summary>
    Unknown
}