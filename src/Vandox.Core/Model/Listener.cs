using System.Net;
using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// A socket without a remote endpoint.
/// </summary>
public sealed class Listener
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

    /// <summary>
    /// Gets or sets the UID of the socket from the kernel table; <c>null</c> when unknown.
    /// </summary>
    [JsonPropertyName("uid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Uid { get; set; }

    /// <summary>
    /// Gets or sets the name of the UID, empty when absent.
    /// </summary>
    [JsonPropertyName("user")]
    [OmitWhenEmpty]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether sockets of several UIDs share the endpoint.
    /// </summary>
    [JsonPropertyName("mixed_uids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MixedUids { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        return ConnectionSnapshot.CheckProtoOnly(path, Proto)
                   ?? Check.AddressPort($"{path}.local", Local)
                   ?? ConnectionSnapshot.CheckProcess(path, Pid, Command)
                   ?? ConnectionSnapshot.CheckSocketUser(path, Uid, User, MixedUids);
    }

    #endregion // Methods
}