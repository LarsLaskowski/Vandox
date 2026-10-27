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
    public const int SchemaVersion = 12;

    /// <summary>
    /// The largest size of the serialized data of one incident in UTF-8 bytes.
    /// </summary>
    public const int MaxIncidentDataBytes = 8388608;

    /// <summary>
    /// The most UTF-8 bytes of a payload stored as JSON.
    /// </summary>
    public const int MaxPayloadBytes = 16777216;

    /// <summary>
    /// The largest number of bases of one content.
    /// </summary>
    public const int MaxImportBases = 64;

    /// <summary>
    /// The largest number of line hashes one row holds.
    /// </summary>
    public const int MaxLineHashes = 4096;

    /// <summary>
    /// The number of bytes of one line hash.
    /// </summary>
    public const int LineHashBytes = 16;

    /// <summary>
    /// The largest number of complete contents compared when a content is checked for being the start of one.
    /// </summary>
    public const int MaxImportContainers = 64;

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

    /// <summary>
    /// The longest range of a usual-level query in hours.
    /// </summary>
    public const int MaxSpikeRangeHours = 744;

    /// <summary>
    /// The largest number of baseline hours of a usual-level query.
    /// </summary>
    public const int MaxBaselineHours = 2160;

    /// <summary>
    /// The largest factor of a usual-level query.
    /// </summary>
    public const double MaxSpikeFactor = 1000;

    /// <summary>
    /// The default number of baseline hours.
    /// </summary>
    public const int DefaultBaselineHours = 168;

    /// <summary>
    /// The default fewest baseline hours with lines of the source.
    /// </summary>
    public const int DefaultMinBaselineHours = 24;

    /// <summary>
    /// The default factor above the usual level.
    /// </summary>
    public const double DefaultSpikeFactor = 3;

    /// <summary>
    /// The default fewest lines in an hour to be reported.
    /// </summary>
    public const long DefaultSpikeMinCount = 10;

    /// <summary>
    /// The number of stored log lines a template rebuild counts in one transaction.
    /// </summary>
    public const int TemplateRebuildBatchLines = 10000;

    /// <summary>
    /// The largest limit of a log exploration page.
    /// </summary>
    public const int MaxFindLimit = 1000;

    /// <summary>
    /// The largest scan limit of a log exploration call.
    /// </summary>
    public const int MaxScanRows = 100000;

    /// <summary>
    /// The largest number of context lines on each side of a log line.
    /// </summary>
    public const int MaxContextLines = 20;

    /// <summary>
    /// The largest number of log lines examined on each side of a context.
    /// </summary>
    public const int ContextScanRows = 2000;

    /// <summary>
    /// The largest number of sources the source list returns.
    /// </summary>
    public const int MaxLogSources = 64;

    /// <summary>
    /// The largest limit of a template list.
    /// </summary>
    public const int MaxTemplateListLimit = 1000;

    /// <summary>
    /// The largest number of UTF-16 characters of an incident note.
    /// </summary>
    public const int MaxNoteChars = 1800;

    /// <summary>
    /// The largest limit of an incident list page.
    /// </summary>
    public const int MaxIncidentListLimit = 100;

    /// <summary>
    /// The largest number of notes read for one incident.
    /// </summary>
    public const int MaxNotesRead = 64;

    #endregion // Constants
}