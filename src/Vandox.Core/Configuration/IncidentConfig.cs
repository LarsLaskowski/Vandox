namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the incident reconstruction.
/// </summary>
public sealed class IncidentConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets how long before an OOM kill an incident starts, such as <c>1h</c> or <c>30m</c>.
    /// </summary>
    [ConfigKey("window_before")]
    public string WindowBefore { get; set; } = "1h";

    /// <summary>
    /// Gets or sets how long after an OOM kill an incident ends, such as <c>30m</c>.
    /// </summary>
    [ConfigKey("window_after")]
    public string WindowAfter { get; set; } = "30m";

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the window before a kill.
    /// </summary>
    /// <returns>The duration</returns>
    /// <exception cref="InvalidOperationException">The option does not parse</exception>
    public TimeSpan GetWindowBefore()
    {
        return Parse(WindowBefore, "incidents.window_before");
    }

    /// <summary>
    /// Returns the window after a kill.
    /// </summary>
    /// <returns>The duration</returns>
    /// <exception cref="InvalidOperationException">The option does not parse</exception>
    public TimeSpan GetWindowAfter()
    {
        return Parse(WindowAfter, "incidents.window_after");
    }

    /// <summary>
    /// Parses one option.
    /// </summary>
    /// <param name="text">The option value</param>
    /// <param name="key">The key path, for the error message</param>
    /// <returns>The duration</returns>
    private static TimeSpan Parse(string text, string key)
    {
        if (ConfigDuration.TryParse(text, out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"config: {key} is not valid");
    }

    #endregion // Methods
}