namespace Vandox.Import;

/// <summary>
/// What happened to a file.
/// </summary>
public enum ImportOutcome
{
    /// <summary>
    /// No outcome yet.
    /// </summary>
    None = 0,

    /// <summary>
    /// The records of the file were stored in this run.
    /// </summary>
    Imported,

    /// <summary>
    /// The content of the file was imported completely before.
    /// </summary>
    AlreadyImported,

    /// <summary>
    /// The file was listed but not imported.
    /// </summary>
    Unrecognized,

    /// <summary>
    /// The file could not be imported.
    /// </summary>
    Failed
}