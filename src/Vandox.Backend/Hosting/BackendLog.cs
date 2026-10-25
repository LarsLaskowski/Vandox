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

    /// <summary>
    /// Logs the raw lines kept of a file.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="path">The display path of the file</param>
    /// <param name="sourceType">The source type of the file</param>
    /// <param name="raw">The raw lines kept</param>
    [LoggerMessage(EventId = 29, Level = LogLevel.Warning, Message = "raw lines kept {Path} {SourceType} {Raw}")]
    internal static partial void RawLinesKept(this ILogger logger, string path, string sourceType, long raw);

    /// <summary>
    /// Logs the start of an incident reconstruction.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "incident reconstruction started")]
    internal static partial void IncidentsStarted(this ILogger logger);

    /// <summary>
    /// Logs an interrupted incident reconstruction.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "incident reconstruction interrupted; run it again to complete it")]
    internal static partial void IncidentsInterrupted(this ILogger logger);

    /// <summary>
    /// Logs a failed incident reconstruction.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    /// <param name="error">The text of the failure</param>
    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "incident reconstruction failed {Error}")]
    internal static partial void IncidentsFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs an incident the store refused.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="firstKillAt">The time of the first kill, in the round-trip format</param>
    /// <param name="error">The text the store gave</param>
    [LoggerMessage(EventId = 33, Level = LogLevel.Warning, Message = "incident skipped {FirstKillAt} {Error}")]
    internal static partial void IncidentSkipped(this ILogger logger, string firstKillAt, string error);

    /// <summary>
    /// Logs the start of a template rebuild.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 34, Level = LogLevel.Information, Message = "template rebuild started")]
    internal static partial void TemplateRebuildStarted(this ILogger logger);

    /// <summary>
    /// Logs an interrupted template rebuild.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 35, Level = LogLevel.Warning, Message = "template rebuild interrupted; the template statistics are incomplete until a rebuild completes")]
    internal static partial void TemplateRebuildInterrupted(this ILogger logger);

    /// <summary>
    /// Logs a failed template rebuild.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    /// <param name="error">The text of the failure</param>
    [LoggerMessage(EventId = 36, Level = LogLevel.Error, Message = "template rebuild failed {Error}")]
    internal static partial void TemplateRebuildFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs the progress of a template rebuild.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="lines">The log lines counted so far</param>
    [LoggerMessage(EventId = 37, Level = LogLevel.Information, Message = "template rebuild progress {Lines}")]
    internal static partial void TemplateRebuildProgress(this ILogger logger, long lines);

    /// <summary>
    /// Logs the start of a forensics report.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 38, Level = LogLevel.Information, Message = "report started")]
    internal static partial void ReportStarted(this ILogger logger);

    /// <summary>
    /// Logs an interrupted forensics report.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 39, Level = LogLevel.Warning, Message = "report interrupted; the report on standard output is incomplete")]
    internal static partial void ReportInterrupted(this ILogger logger);

    /// <summary>
    /// Logs a failed forensics report.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    /// <param name="error">The text of the failure</param>
    [LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "report failed {Error}")]
    internal static partial void ReportFailed(this ILogger logger, Exception exception, string error);

    /// <summary>
    /// Logs a failed read of the logs view.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    [LoggerMessage(EventId = 49, Level = LogLevel.Error, Message = "logs view: reading failed")]
    internal static partial void LogsReadFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs that the web password hash is not set.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="variable">The variable that holds the hash</param>
    [LoggerMessage(EventId = 41, Level = LogLevel.Error, Message = "web password hash missing {Variable}")]
    internal static partial void WebPasswordHashMissing(this ILogger logger, string variable);

    /// <summary>
    /// Logs that the web password hash is not an accepted argon2id hash.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="reason">Why the hash is refused</param>
    [LoggerMessage(EventId = 42, Level = LogLevel.Error, Message = "web password hash invalid {Reason}")]
    internal static partial void WebPasswordHashInvalid(this ILogger logger, string reason);

    /// <summary>
    /// Logs a successful sign-in.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="client">The client address</param>
    [LoggerMessage(EventId = 43, Level = LogLevel.Information, Message = "login succeeded {Client}")]
    internal static partial void LoginSucceeded(this ILogger logger, string client);

    /// <summary>
    /// Logs a failed sign-in.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="client">The client address</param>
    /// <param name="failures">The failures of the client within the window</param>
    [LoggerMessage(EventId = 44, Level = LogLevel.Warning, Message = "login failed {Client} {Failures}")]
    internal static partial void LoginFailed(this ILogger logger, string client, int failures);

    /// <summary>
    /// Logs that a client is locked out.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="client">The client address</param>
    /// <param name="until">The end of the lock, UTC RFC 3339</param>
    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "login locked {Client} {Until}")]
    internal static partial void LoginLocked(this ILogger logger, string client, string until);

    /// <summary>
    /// Logs that every client is locked out.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="until">The end of the lock, UTC RFC 3339</param>
    [LoggerMessage(EventId = 46, Level = LogLevel.Warning, Message = "login locked for all clients {Until}")]
    internal static partial void LoginLockedForAll(this ILogger logger, string until);

    /// <summary>
    /// Logs a sign-out.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="client">The client address</param>
    [LoggerMessage(EventId = 47, Level = LogLevel.Information, Message = "logged out {Client}")]
    internal static partial void LoggedOut(this ILogger logger, string client);

    /// <summary>
    /// Logs that the request carried an <c>X-Forwarded-For</c> header that was not applied.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="client">The client address</param>
    [LoggerMessage(EventId = 48, Level = LogLevel.Warning, Message = "forwarded header ignored {Client}")]
    internal static partial void ForwardedHeaderIgnored(this ILogger logger, string client);

    /// <summary>
    /// Logs a failed read of the incidents view.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    [LoggerMessage(EventId = 50, Level = LogLevel.Error, Message = "incidents view: reading failed")]
    internal static partial void IncidentsReadFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs a failed save of an incident note; the note text and the form values stay out of the log.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    [LoggerMessage(EventId = 51, Level = LogLevel.Error, Message = "incidents view: saving a note failed")]
    internal static partial void IncidentNoteSaveFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs that making the report for the page failed.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    [LoggerMessage(EventId = 52, Level = LogLevel.Error, Message = "report page: making the report failed")]
    internal static partial void ReportPageFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs that making the report for the download failed.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The failure</param>
    [LoggerMessage(EventId = 53, Level = LogLevel.Error, Message = "report download: making the report failed")]
    internal static partial void ReportDownloadFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logs that ingest is disabled because a setting is missing.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="missing">The names of the missing settings</param>
    [LoggerMessage(EventId = 54, Level = LogLevel.Warning, Message = "ingest disabled {Missing}")]
    internal static partial void IngestDisabled(this ILogger logger, string missing);

    /// <summary>
    /// Logs a refused ingest token; the token itself is never logged.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="peer">The peer address</param>
    /// <param name="reason">The fixed reason</param>
    /// <param name="suppressed">The refusals suppressed since the last line</param>
    [LoggerMessage(EventId = 55, Level = LogLevel.Warning, Message = "ingest token refused {Peer} {Reason} {Suppressed}")]
    internal static partial void IngestTokenRefused(this ILogger logger, string peer, string reason, int suppressed);

    /// <summary>
    /// Logs a batch of another agent than the one the token is bound to.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="agentId">The agent ID of the batch header</param>
    /// <param name="peer">The peer address</param>
    /// <param name="suppressed">The refusals suppressed since the last line</param>
    [LoggerMessage(EventId = 56, Level = LogLevel.Warning, Message = "ingest agent refused {AgentId} {Peer} {Suppressed}")]
    internal static partial void IngestAgentRefused(this ILogger logger, string agentId, string peer, int suppressed);

    /// <summary>
    /// Logs a refused batch; only fixed reasons, a line number and a reduced field path.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="agentId">The agent ID of the batch header, when it was read</param>
    /// <param name="status">The HTTP status</param>
    /// <param name="reason">The fixed reason</param>
    /// <param name="line">The line number, when known</param>
    /// <param name="field">The reduced field path, when it parsed</param>
    [LoggerMessage(EventId = 57, Level = LogLevel.Warning, Message = "ingest batch refused {AgentId} {Status} {Reason} {Line} {Field}")]
    internal static partial void IngestBatchRefused(this ILogger logger, string? agentId, int status, string reason, long? line, string? field);

    /// <summary>
    /// Logs a request that was not accepted and is not a refusal of the batch.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="status">The HTTP status</param>
    /// <param name="reason">The fixed reason</param>
    [LoggerMessage(EventId = 58, Level = LogLevel.Warning, Message = "ingest request not accepted {Status} {Reason}")]
    internal static partial void IngestRequestNotAccepted(this ILogger logger, int status, string reason);

    /// <summary>
    /// Logs a request that passed its time budget.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="peer">The peer address</param>
    [LoggerMessage(EventId = 59, Level = LogLevel.Warning, Message = "ingest request timed out {Peer}")]
    internal static partial void IngestRequestTimedOut(this ILogger logger, string peer);

    /// <summary>
    /// Logs a failed write of a batch; the message of the failure is never logged.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="exception">The type name of the failure</param>
    [LoggerMessage(EventId = 60, Level = LogLevel.Error, Message = "ingest store failed {AgentId} {Exception}")]
    internal static partial void IngestStoreFailed(this ILogger logger, string agentId, string exception);

    /// <summary>
    /// Logs an unexpected failure of a request; the message of the failure is never logged.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="exception">The type name of the failure</param>
    [LoggerMessage(EventId = 61, Level = LogLevel.Error, Message = "ingest request failed {Exception}")]
    internal static partial void IngestRequestFailed(this ILogger logger, string exception);

    /// <summary>
    /// Logs that ingest is paused because the free space fell below the floor.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="availableBytes">The free bytes; none when they could not be read</param>
    [LoggerMessage(EventId = 62, Level = LogLevel.Warning, Message = "ingest paused: free space low {AvailableBytes}")]
    internal static partial void IngestPaused(this ILogger logger, long? availableBytes);

    /// <summary>
    /// Logs that ingest resumed after a pause.
    /// </summary>
    /// <param name="logger">The logger</param>
    [LoggerMessage(EventId = 63, Level = LogLevel.Information, Message = "ingest resumed")]
    internal static partial void IngestResumed(this ILogger logger);

    /// <summary>
    /// Logs the first stored batch of the process.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="agentId">The agent ID</param>
    [LoggerMessage(EventId = 64, Level = LogLevel.Information, Message = "ingest first batch stored {AgentId}")]
    internal static partial void IngestFirstBatchStored(this ILogger logger, string agentId);

    /// <summary>
    /// Logs a stored batch.
    /// </summary>
    /// <param name="logger">The logger</param>
    /// <param name="agentId">The agent ID</param>
    /// <param name="mode">The mode of the batch header</param>
    /// <param name="records">The records of the batch</param>
    /// <param name="stored">The records stored</param>
    /// <param name="duplicates">The records that were already present</param>
    [LoggerMessage(EventId = 65, Level = LogLevel.Debug, Message = "ingest batch stored {AgentId} {Mode} {Records} {Stored} {Duplicates}")]
    internal static partial void IngestBatchStored(this ILogger logger, string agentId, string mode, int records, int stored, int duplicates);

    #endregion // Methods
}