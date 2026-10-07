namespace Vandox.Import;

/// <summary>
/// Reports the state of a run.
/// </summary>
public sealed class ImportProgress
{
    #region Properties

    /// <summary>
    /// Gets or sets the event.
    /// </summary>
    public ProgressEvent Event { get; set; }

    /// <summary>
    /// Gets or sets the number of files found; with <see cref="ProgressEvent.Scanned"/>.
    /// </summary>
    public int Files { get; set; }

    /// <summary>
    /// Gets or sets the number of files to import; with <see cref="ProgressEvent.Scanned"/>.
    /// </summary>
    public int Pending { get; set; }

    /// <summary>
    /// Gets or sets the display path of the file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source type of the file.
    /// </summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of lines read.
    /// </summary>
    public long Lines { get; set; }

    /// <summary>
    /// Gets or sets the number of records stored.
    /// </summary>
    public long Records { get; set; }

    /// <summary>
    /// Gets or sets the decompressed bytes of the file read so far; with <see cref="ProgressEvent.ScanProgress"/>.
    /// </summary>
    public long Bytes { get; set; }

    /// <summary>
    /// Gets or sets the result of the file; with <see cref="ProgressEvent.FileFinished"/>.
    /// </summary>
    public FileResult? Result { get; set; }

    #endregion // Properties
}