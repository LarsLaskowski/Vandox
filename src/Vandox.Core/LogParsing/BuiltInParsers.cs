namespace Vandox.Core.LogParsing;

/// <summary>
/// The parsers that ship with vandoxd.
/// </summary>
public static class BuiltInParsers
{
    #region Methods

    /// <summary>
    /// Creates the built-in parsers in registry order: the journal export, then the generic syslog parser.
    /// </summary>
    /// <param name="timeZone">The IANA time zone for year-less log lines; <c>null</c> when not set</param>
    /// <returns>The parsers</returns>
    /// <exception cref="ArgumentException">The zone is unknown</exception>
    public static IReadOnlyList<ILogParser> Create(string? timeZone)
    {
        var zone = timeZone is null ? null : SourceTimeZone.Find(timeZone);

        if (timeZone is not null && zone is null)
        {
            throw new ArgumentException("logparse: the time zone is not a time zone of the IANA time zone database", nameof(timeZone));
        }

        return [new JournalExportParser(), new SyslogParser(zone)];
    }

    #endregion // Methods
}