using System.Net;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// A socket with a remote endpoint.
/// </summary>
public sealed class Connection
{
    #region Properties

    /// <summary>
    /// Gets or sets the protocol, one of the <see cref="Proto"/> constants.
    /// </summary>
    [JsonPropertyName("proto")]
    public string Proto { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the local endpoint.
    /// </summary>
    [JsonPropertyName("local")]
    [JsonConverter(typeof(IpEndPointJsonConverter))]
    public IPEndPoint? Local { get; set; }

    /// <summary>
    /// Gets or sets the remote endpoint.
    /// </summary>
    [JsonPropertyName("remote")]
    [JsonConverter(typeof(IpEndPointJsonConverter))]
    public IPEndPoint? Remote { get; set; }

    /// <summary>
    /// Gets or sets the TCP state; empty for UDP.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the process ID of the owner.
    /// </summary>
    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the command of the owner.
    /// </summary>
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Command { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        return ConnectionSnapshot.CheckProto(path, Proto, State)
                   ?? Check.AddressPort($"{path}.local", Local)
                   ?? Check.AddressPort($"{path}.remote", Remote)
                   ?? ConnectionSnapshot.CheckProcess(path, Pid, Command);
    }

    #endregion // Methods
}