namespace Vandox.Import;

/// <summary>
/// Limits and defaults of an import run.
/// </summary>
public static class ImportLimits
{
    #region Constants

    /// <summary>
    /// The number of entries one run handles: directory entries of any kind and tar headers except PAX global headers;
    /// enforced while scanning.
    /// </summary>
    public const int MaxFiles = 20000;

    /// <summary>
    /// The longest relative path or entry name in UTF-8 bytes.
    /// </summary>
    public const int MaxPathBytes = 1024;

    /// <summary>
    /// The default number of records per batch.
    /// </summary>
    public const int DefaultBatchRecords = 2000;

    /// <summary>
    /// The default number of input bytes per batch.
    /// </summary>
    public const long DefaultBatchBytes = 4L << 20;

    /// <summary>
    /// The number of problems kept per file.
    /// </summary>
    public const int MaxProblems = 10;

    /// <summary>
    /// The number of lines between two file progress events.
    /// </summary>
    public const long ProgressLines = 100000;

    /// <summary>
    /// The default number of decompressed bytes between two scan progress events while a file is hashed.
    /// </summary>
    public const long DefaultProgressBytes = 64L << 20;

    #endregion // Constants
}