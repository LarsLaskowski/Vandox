namespace Vandox.Storage;

/// <summary>
/// Ties a batch to the import of one file.
/// </summary>
public sealed class ImportStep
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the import file.
    /// </summary>
    public long FileId { get; set; }

    /// <summary>
    /// Gets or sets the number of records of the file stored before this batch; it must equal the stored count.
    /// </summary>
    public long Done { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file is completely imported after this batch.
    /// </summary>
    public bool Complete { get; set; }

    #endregion // Properties
}