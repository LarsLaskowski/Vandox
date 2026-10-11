namespace Vandox.Core.LogParsing;

/// <summary>
/// The configured paths of the error logs the backend recognizes by their location.
/// </summary>
/// <param name="PleskPanel">The path of the Plesk panel log</param>
/// <param name="SwCpServer">The path of the sw-cp-server error log</param>
/// <param name="Nginx">The path of the nginx error log</param>
/// <param name="Apache">The path of the Apache error log</param>
public sealed record ErrorLogPaths(string PleskPanel, string SwCpServer, string Nginx, string Apache)
{
    #region Constants

    /// <summary>
    /// The default path of the Plesk panel log.
    /// </summary>
    public const string DefaultPleskPanel = "/var/log/plesk/panel.log";

    /// <summary>
    /// The default path of the sw-cp-server error log.
    /// </summary>
    public const string DefaultSwCpServer = "/var/log/sw-cp-server/error_log";

    /// <summary>
    /// The default path of the nginx error log.
    /// </summary>
    public const string DefaultNginx = "/var/log/nginx/error.log";

    /// <summary>
    /// The default path of the Apache error log.
    /// </summary>
    public const string DefaultApache = "/var/log/apache2/error.log";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the paths with their defaults.
    /// </summary>
    public static ErrorLogPaths Default { get; } = new(DefaultPleskPanel, DefaultSwCpServer, DefaultNginx, DefaultApache);

    #endregion // Properties
}