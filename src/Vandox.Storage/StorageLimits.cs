namespace Vandox.Storage;

/// <summary>
/// Limits of the storage layer.
/// </summary>
public static class StorageLimits
{
    #region Constants

    /// <summary>
    /// The name of the database file in the storage directory.
    /// </summary>
    public const string FileName = "vandox.db";

    /// <summary>
    /// The database schema version this build reads and writes.
    /// </summary>
    public const int SchemaVersion = 5;

    /// <summary>
    /// The largest number of records a batch holds.
    /// </summary>
    public const int MaxBatchRecords = 20000;

    /// <summary>
    /// The longest file name an import stores; longer names are cut.
    /// </summary>
    public const int MaxImportNameBytes = 1024;

    /// <summary>
    /// The largest limit of a record query or log search.
    /// </summary>
    public const int MaxQueryLimit = 10000;

    /// <summary>
    /// The largest number of terms in a log search text.
    /// </summary>
    public const int MaxSearchTerms = 16;

    /// <summary>
    /// The largest size of a log search text in UTF-8 bytes.
    /// </summary>
    public const int MaxSearchBytes = 1024;

    #endregion // Constants
}