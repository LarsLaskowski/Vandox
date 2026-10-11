namespace Vandox.Storage;

/// <summary>
/// Records which file contents have been imported.
/// </summary>
public interface IImportTracker
{
    #region Methods

    /// <summary>
    /// Returns the import state of a content, creating it with attempt 1 when it is unknown, unless the content is the start
    /// of a complete stored content, in which case no state is created and the returned <see cref="ImportFile"/> has
    /// <see cref="ImportFile.ContainedIn"/> set (not empty) and no other member set; a caller checks
    /// <see cref="ImportFile.ContainedIn"/> before any other member. An existing incomplete state is returned with its attempt
    /// advanced by one, and the line hashes of its earlier attempts are deleted; a complete state is returned unchanged; both
    /// have an empty <see cref="ImportFile.ContainedIn"/>.
    /// </summary>
    /// <param name="start">The content the importer is about to import</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the import state</returns>
    /// <exception cref="InvalidImportException">The start breaks a rule</exception>
    Task<ImportFile> BeginImportAsync(ImportFileStart start, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the completely imported contents with a head key and source type, largest first, at most
    /// <see cref="StorageLimits.MaxImportBases"/>.
    /// </summary>
    /// <param name="headSha256">The head key; 32 bytes</param>
    /// <param name="sourceType">The parser type</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the contents</returns>
    /// <exception cref="InvalidImportException">The head key or the source type breaks a rule</exception>
    Task<IReadOnlyList<ImportBase>> FindImportBasesAsync(byte[] headSha256, string sourceType, CancellationToken cancellationToken);

    /// <summary>
    /// Stores one row of line hashes of an incomplete content.
    /// </summary>
    /// <param name="hashes">The row</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the row is stored</returns>
    /// <exception cref="InvalidImportException">The row breaks a rule</exception>
    /// <exception cref="ImportConflictException">The file is complete or unknown, or was begun again with a later attempt</exception>
    Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken);

    #endregion // Methods
}