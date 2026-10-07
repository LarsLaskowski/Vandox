namespace Vandox.Import;

/// <summary>
/// Names a progress event.
/// </summary>
public enum ProgressEvent
{
    /// <summary>
    /// Pass 1: the decompressed bytes of a file that is being hashed.
    /// </summary>
    ScanProgress,

    /// <summary>
    /// Pass 1 is done.
    /// </summary>
    Scanned,

    /// <summary>
    /// Pass 2 starts a file.
    /// </summary>
    FileStarted,

    /// <summary>
    /// Pass 2 read another <see cref="ImportLimits.ProgressLines"/> lines of a file.
    /// </summary>
    FileProgress,

    /// <summary>
    /// A file is finished or listed.
    /// </summary>
    FileFinished
}