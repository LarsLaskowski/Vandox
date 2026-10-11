using Vandox.Core.LogParsing;

namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the log import.
/// </summary>
public sealed class ImportConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the path of the Plesk panel log.
    /// </summary>
    [ConfigKey("plesk_panel_log")]
    public string PleskPanelLog { get; set; } = ErrorLogPaths.DefaultPleskPanel;

    /// <summary>
    /// Gets or sets the path of the sw-cp-server error log.
    /// </summary>
    [ConfigKey("sw_cp_server_error_log")]
    public string SwCpServerErrorLog { get; set; } = ErrorLogPaths.DefaultSwCpServer;

    /// <summary>
    /// Gets or sets the path of the nginx error log.
    /// </summary>
    [ConfigKey("nginx_error_log")]
    public string NginxErrorLog { get; set; } = ErrorLogPaths.DefaultNginx;

    /// <summary>
    /// Gets or sets the path of the Apache error log.
    /// </summary>
    [ConfigKey("apache_error_log")]
    public string ApacheErrorLog { get; set; } = ErrorLogPaths.DefaultApache;

    /// <summary>
    /// Gets or sets the IANA time zone of the imported server for log lines without a year or offset; <c>null</c> when not set.
    /// </summary>
    [ConfigKey("time_zone")]
    public string? TimeZone { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the configured paths of the error logs.
    /// </summary>
    /// <returns>The paths</returns>
    public ErrorLogPaths GetErrorLogPaths()
    {
        return new ErrorLogPaths(PleskPanelLog, SwCpServerErrorLog, NginxErrorLog, ApacheErrorLog);
    }

    #endregion // Methods
}