namespace Vandox.Core.LogParsing;

/// <summary>
/// The parsers that ship with vandoxd.
/// </summary>
public static class BuiltInParsers
{
    #region Methods

    /// <summary>
    /// Creates the built-in parsers in registry order: the journal export, the MariaDB error log, Dovecot's own log file, the generic syslog parser, then the error log parsers and the legacy monitoring log parser.
    /// </summary>
    /// <param name="timeZone">The IANA time zone for year-less log lines and the local times of the MariaDB error log; <c>null</c> when not set</param>
    /// <param name="paths">The configured paths of the error logs</param>
    /// <returns>The parsers</returns>
    /// <exception cref="ArgumentException">The zone is unknown</exception>
    public static IReadOnlyList<ILogParser> Create(string? timeZone, ErrorLogPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var zone = timeZone is null ? null : SourceTimeZone.Find(timeZone);

        if (timeZone is not null && zone is null)
        {
            throw new ArgumentException("logparse: the time zone is not a time zone of the IANA time zone database", nameof(timeZone));
        }

        return
        [
            new JournalExportParser(),
            new MariaDbErrorLogParser(zone),
            new DovecotLogParser(zone),
            new SyslogParser(zone),
            new ErrorLogParser(ErrorLogParser.PleskPanelType, ErrorLogFormat.PleskPanel, paths.PleskPanel, true, zone),
            new ErrorLogParser(ErrorLogParser.SwCpServerType, ErrorLogFormat.Nginx, paths.SwCpServer, false, zone),
            new ErrorLogParser(ErrorLogParser.NginxType, ErrorLogFormat.Nginx, paths.Nginx, true, zone),
            new ErrorLogParser(ErrorLogParser.ApacheType, ErrorLogFormat.Apache, paths.Apache, true, zone),
            new LegacyMonitorParser(zone)
        ];
    }

    #endregion // Methods
}