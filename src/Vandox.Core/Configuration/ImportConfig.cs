namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the log import.
/// </summary>
public sealed class ImportConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the IANA time zone of the imported server for log lines without a year or offset; <c>null</c> when not set.
    /// </summary>
    [ConfigKey("time_zone")]
    public string? TimeZone { get; set; }

    #endregion // Properties
}