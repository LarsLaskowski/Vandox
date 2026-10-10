namespace Vandox.Core.LogParsing;

/// <summary>
/// The kept fields of one journal export entry.
/// </summary>
internal sealed class JournalEntry
{
    #region Properties

    /// <summary>
    /// Gets or sets the value of <c>__REALTIME_TIMESTAMP</c>.
    /// </summary>
    internal string? Realtime { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>_HOSTNAME</c>.
    /// </summary>
    internal string? Hostname { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>SYSLOG_IDENTIFIER</c>.
    /// </summary>
    internal string? SyslogIdentifier { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>_COMM</c>.
    /// </summary>
    internal string? Comm { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>_PID</c>.
    /// </summary>
    internal string? Pid { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>SYSLOG_PID</c>.
    /// </summary>
    internal string? SyslogPid { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>PRIORITY</c>.
    /// </summary>
    internal string? Priority { get; set; }

    /// <summary>
    /// Gets or sets the value of <c>MESSAGE</c>.
    /// </summary>
    internal string? Message { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>MESSAGE</c> or a kept short field was cut.
    /// </summary>
    internal bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets the fixed skip reason when the entry is malformed or cut off.
    /// </summary>
    internal string? Problem { get; set; }

    #endregion // Properties
}