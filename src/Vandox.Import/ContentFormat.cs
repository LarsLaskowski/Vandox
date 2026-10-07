namespace Vandox.Import;

/// <summary>
/// The container format of a file's content.
/// </summary>
internal enum ContentFormat
{
    /// <summary>
    /// Plain content: a log.
    /// </summary>
    Plain,

    /// <summary>
    /// No content.
    /// </summary>
    Empty,

    /// <summary>
    /// A gzip stream.
    /// </summary>
    Gzip,

    /// <summary>
    /// A tar archive.
    /// </summary>
    Tar,

    /// <summary>
    /// A compression or archive format that is listed, not imported.
    /// </summary>
    Unsupported
}