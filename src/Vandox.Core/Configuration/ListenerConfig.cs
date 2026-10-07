namespace Vandox.Core.Configuration;

/// <summary>
/// A listen address.
/// </summary>
public sealed class ListenerConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the address as <c>[host]:port</c>; the host is empty or an IP address.
    /// </summary>
    [ConfigKey("listen")]
    public string Listen { get; set; } = string.Empty;

    #endregion // Properties
}