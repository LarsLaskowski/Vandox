namespace Vandox.Core.IO;

/// <summary>
/// The type of a file system entry.
/// </summary>
public enum FileKind
{
    /// <summary>
    /// The entry does not exist.
    /// </summary>
    Missing,

    /// <summary>
    /// A regular file.
    /// </summary>
    Regular,

    /// <summary>
    /// A directory.
    /// </summary>
    Directory,

    /// <summary>
    /// A symbolic link.
    /// </summary>
    Symlink,

    /// <summary>
    /// A FIFO, socket, device or any other special file.
    /// </summary>
    Other
}