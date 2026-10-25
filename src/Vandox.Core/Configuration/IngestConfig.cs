#pragma warning disable RH2003, S2325

namespace Vandox.Core.Configuration;

/// <summary>
/// Configures the ingest listener and the agent it accepts.
/// </summary>
public sealed class IngestConfig
{
    #region Properties

    /// <summary>
    /// Gets or sets the address as <c>[host]:port</c>; the host is empty or an IP address.
    /// </summary>
    [ConfigKey("listen")]
    public string Listen { get; set; } = ":8081";

    /// <summary>
    /// Gets or sets the agent ID the ingest token is bound to; <c>null</c> while unset.
    /// </summary>
    [ConfigKey("agent_id")]
    public string? AgentId { get; set; }

    /// <summary>
    /// Gets or sets the free space of the storage volume below which ingest pauses, such as <c>1GiB</c>.
    /// </summary>
    [ConfigKey("min_free")]
    public string MinFree { get; set; } = "1GiB";

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns <see cref="MinFree"/> in bytes.
    /// </summary>
    /// <returns>The number of bytes; zero switches the check off</returns>
    public long GetMinFreeBytes()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}