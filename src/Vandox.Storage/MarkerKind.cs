namespace Vandox.Storage;

/// <summary>
/// The kind of a marker.
/// </summary>
public enum MarkerKind
{
    /// <summary>
    /// An out-of-memory kill.
    /// </summary>
    OomKill,

    /// <summary>
    /// A boot.
    /// </summary>
    Boot,

    /// <summary>
    /// A service restart.
    /// </summary>
    ServiceRestart,

    /// <summary>
    /// A backup run.
    /// </summary>
    BackupRun
}