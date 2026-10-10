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
                   ?? ConnectionSnapshot.CheckProcess(path, Pid, Command);
    }

    #endregion // Methods
}