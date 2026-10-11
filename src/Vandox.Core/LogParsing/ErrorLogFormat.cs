namespace Vandox.Core.LogParsing;

/// <summary>
/// The line format of an error log.
/// </summary>
public enum ErrorLogFormat
{
    /// <summary>
    /// The Plesk panel log.
    /// </summary>
    PleskPanel = 0,

    /// <summary>
    /// The nginx error log, also used by sw-cp-server.
    /// </summary>
    Nginx = 1,

    /// <summary>
    /// The Apache error log.
    /// </summary>
    Apache = 2
}