namespace Vandox.Storage;

/// <summary>
/// Records which file contents have been imported.
/// </summary>
public interface IImportTracker
{
    #region Methods

    /// <summary>
    /// Returns the import state of a content, creating it (no records, not complete) when it is unknown. An existing state
    /// is returned unchanged.
    /// </summary>
    /// <param name="start">The content the importer is about to import</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the import state</returns>
    /// <exception cref="InvalidImportException">The start breaks a rule</exception>
    Task<ImportFile> BeginImportAsync(ImportFileStart start, CancellationToken cancellationToken);

    #endregion // Methods
}