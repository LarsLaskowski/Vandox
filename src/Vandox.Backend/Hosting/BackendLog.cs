using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Hosting;

/// <summary>
/// The log events of vandoxd. A message holds only fixed text and attribute placeholders: the JSON logger writes the text
/// without the placeholders as <c>msg</c> and every placeholder as an attribute.
/// </summary>
internal static partial class BackendLog
{
    #region Methods

    /// <summary>
    /// Logs that the service starts.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="version">The version line</param>
    /// <param name="config">The configuration file</param>
    /// <param name="web">The web listen address</param>
    /// <param name="ingest">The ingest listen address</param>
    /// <param name="storage">The storage directory</param>
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "vandoxd starting {Version} {Config} {Web} {Ingest} {Storage}")]
    internal static partial void Starting(this ILogger logger, string version, string config, string web, string ingest, string storage);

    /// <summary>
    /// Logs that the database is open.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The database file</param>
    /// <param name="created">Whether the file was created</param>
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "database opened {Path} {Created}")]
    internal static partial void DatabaseOpened(this ILogger logger, string path, bool created);

    /// <summary>
    /// Logs that the database could not be opened.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The message of the cause</param>
    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "opening database failed {Error}")]
    internal static partial void OpeningDatabaseFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs that a listener is open.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="listener">The name of the listener</param>
    /// <param name="address">The configured address</param>
    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "listening {Listener} {Address}")]
    internal static partial void Listening(this ILogger logger, string listener, string address);

    /// <summary>
    /// Logs that the service shuts down.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "shutting down")]
    internal static partial void ShuttingDown(this ILogger logger);

    /// <summary>
    /// Logs that the service stopped.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "vandoxd stopped")]
    internal static partial void Stopped(this ILogger logger);

    /// <summary>
    /// Logs that serving failed.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The message of the cause</param>
    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "serving failed {Error}")]
    internal static partial void ServingFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs that the configuration is invalid.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The message of the cause</param>
    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "configuration invalid {Error}")]
    internal static partial void ConfigurationInvalid(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs that the parsers are invalid.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The message of the cause</param>
    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "parsers invalid {Error}")]
    internal static partial void ParsersInvalid(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs that the health check of the database failed.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The type of the cause</param>
    [LoggerMessage(EventId = 10, Level = LogLevel.Warning, Message = "health check failed {Error}")]
    internal static partial void HealthCheckFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs that an import starts.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The path that is imported</param>
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "import started {Path}")]
    internal static partial void ImportStarted(this ILogger logger, string path);

    /// <summary>
    /// Logs that an import was interrupted.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 21, Level = LogLevel.Warning, Message = "import interrupted; run it again to continue")]
    internal static partial void ImportInterrupted(this ILogger logger);

    /// <summary>
    /// Logs that an import failed.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The cause</param>
    /// <param name="error">The message of the cause</param>
    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "import failed {Error}")]
    internal static partial void ImportFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs the progress of hashing a file.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path</param>
    /// <param name="bytes">The bytes read so far</param>
    [LoggerMessage(EventId = 23, Level = LogLevel.Information, Message = "hashing {Path} {Bytes}")]
    internal static partial void Hashing(this ILogger logger, string path, long bytes);

    /// <summary>
    /// Logs that the scan is finished.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="files">The number of files found</param>
    /// <param name="pending">The number of files to import</param>
    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "scan finished {Files} {Pending}")]
    internal static partial void ScanFinished(this ILogger logger, int files, int pending);

    /// <summary>
    /// Logs that a file starts.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path</param>
    /// <param name="sourceType">The source type</param>
    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "file started {Path} {SourceType}")]
    internal static partial void FileStarted(this ILogger logger, string path, string sourceType);

    /// <summary>
    /// Logs the progress of a file.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path</param>
    /// <param name="lines">The lines read</param>
    /// <param name="records">The records stored</param>
    [LoggerMessage(EventId = 26, Level = LogLevel.Information, Message = "file progress {Path} {Lines} {Records}")]
    internal static partial void FileProgress(this ILogger logger, string path, long lines, long records);

    /// <summary>
    /// Logs the result of a file.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path</param>
    /// <param name="outcome">The outcome</param>
    /// <param name="sourceType">The source type</param>
    /// <param name="lines">The lines read</param>
    /// <param name="records">The records stored</param>
    /// <param name="skipped">The lines skipped</param>
    [LoggerMessage(EventId = 27, Level = LogLevel.Information, Message = "file finished {Path} {Outcome} {SourceType} {Lines} {Records} {Skipped}")]
    internal static partial void FileFinished(this ILogger logger, string path, string outcome, string sourceType, long lines, long records, long skipped);

    /// <summary>
    /// Logs why a file was not imported.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path</param>
    /// <param name="reason">Why the file was not imported</param>
    [LoggerMessage(EventId = 28, Level = LogLevel.Information, Message = "file not imported {Path} {Reason}")]
    internal static partial void FileNotImported(this ILogger logger, string path, string reason);

    #endregion // Methods
}