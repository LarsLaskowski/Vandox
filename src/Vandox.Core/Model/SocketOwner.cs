using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The kernel-table owner of a socket entry, shared by listeners and connections.
/// </summary>
public abstract class SocketOwner
{
    #region Properties

    /// <summary>
    /// Gets or sets the UID of the socket from the kernel table; <c>null</c> when unknown.
    /// </summary>
    [JsonPropertyName("uid")]
    [JsonPropertyOrder(100)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Uid { get; set; }

    /// <summary>
    /// Gets or sets the name of the UID, empty when absent.
    /// </summary>
    [JsonPropertyName("user")]
    [JsonPropertyOrder(101)]
    [OmitWhenEmpty]
    public string User { get; set; } = string.Empty;

    #endregion // Properties
}