namespace Vandox.Import;

/// <summary>
/// A file or archive entry the scanner is about to examine.
/// </summary>
internal sealed class ScanItem
{
    #region Properties

    /// <summary>
    /// Gets or sets the path shown to the operator.
    /// </summary>
    internal string Display { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name the parser gets, before a <c>.gz</c> is removed.
    /// </summary>
    internal string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the modification time in UTC; <c>null</c> when unknown.
    /// </summary>
    internal DateTimeOffset? Modified { get; set; }

    /// <summary>
    /// Gets or sets where the content is read again.
    /// </summary>
    internal ImportLocation Location { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether the item is an entry of an archive.
    /// </summary>
    internal bool InArchive { get; set; }

    /// <summary>
    /// Gets or sets the tracker of the raw content of an archive entry; <c>null</c> for a file.
    /// </summary>
    internal ErrorTrackingStream? Tracker { get; set; }

    #endregion // Properties
}