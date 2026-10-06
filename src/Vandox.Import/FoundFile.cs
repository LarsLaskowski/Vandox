using Vandox.Core.LogParsing;

namespace Vandox.Import;

/// <summary>
/// One file of pass 1.
/// </summary>
internal sealed class FoundFile
{
    #region Properties

    /// <summary>
    /// Gets or sets the result: the path, and the outcome and reason when the file is not imported.
    /// </summary>
    internal FileResult Result { get; set; } = new();

    /// <summary>
    /// Gets or sets the file as the parser gets it.
    /// </summary>
    internal LogFile File { get; set; } = new(string.Empty, null);

    /// <summary>
    /// Gets or sets where the content is read again.
    /// </summary>
    internal ImportLocation Location { get; set; } = new();

    /// <summary>
    /// Gets or sets the parser; <c>null</c> when the file is not to be imported.
    /// </summary>
    internal ILogParser? Parser { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 of the decompressed content.
    /// </summary>
    internal byte[] Sum { get; set; } = new byte[32];

    /// <summary>
    /// Gets or sets the size of the decompressed content in bytes.
    /// </summary>
    internal long Size { get; set; }

    #endregion // Properties
}