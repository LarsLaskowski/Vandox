namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the log output.
/// </summary>
public sealed class LogConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the log level: debug, info, warn or error.
    /// </summary>
    [ConfigKey("level")]
    public string Level { get; set; } = "info";

    #endregion // Properties
}