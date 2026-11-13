namespace Vandox.Core.Configuration;

/// <summary>
/// Configures how long each kind of data is kept.
/// </summary>
public sealed class RetentionConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets how long raw metric values are kept, such as <c>30d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("raw")]
    public string Raw { get; set; } = "30d";

    /// <summary>
    /// Gets or sets how long the 5-minute rollups are kept, such as <c>365d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("rollups_5m")]
    public string FiveMinuteRollups { get; set; } = "365d";

    /// <summary>
    /// Gets or sets how long the hourly rollups are kept, such as <c>1095d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("rollups_1h")]
    public string HourlyRollups { get; set; } = "1095d";

    /// <summary>
    /// Gets or sets how long log lines are kept, such as <c>90d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("logs")]
    public string Logs { get; set; } = "90d";

    /// <summary>
    /// Gets or sets how long snapshots are kept, such as <c>30d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("snapshots")]
    public string Snapshots { get; set; } = "30d";

    /// <summary>
    /// Gets or sets how long events are kept, such as <c>1095d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("events")]
    public string Events { get; set; } = "1095d";

    /// <summary>
    /// Gets or sets how long incidents are kept, such as <c>365d</c> or <c>unlimited</c>.
    /// </summary>
    [ConfigKey("incidents")]
    public string Incidents { get; set; } = "unlimited";

    #endregion // Properties
}