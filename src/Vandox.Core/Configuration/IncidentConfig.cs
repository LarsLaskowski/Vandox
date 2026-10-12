namespace Vandox.Core.Configuration;

#pragma warning disable RH2003, S2325

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
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the window after a kill.
    /// </summary>
    /// <returns>The duration</returns>
    /// <exception cref="InvalidOperationException">The option does not parse</exception>
    public TimeSpan GetWindowAfter()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}