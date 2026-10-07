using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Counts sockets per protocol and state.
/// </summary>
public sealed class StateCount
{
    #region Properties

    /// <summary>
    /// Gets or sets the protocol, one of the <see cref="Proto"/> constants.
    /// </summary>
    [JsonPropertyName("proto")]
    public string Proto { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TCP state; empty for UDP.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of sockets.
    /// </summary>
    [JsonPropertyName("count")]
    public uint Count { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        return ConnectionSnapshot.CheckProto(path, Proto, State) ?? ConnectionSnapshot.CheckCount(path, Count);
    }

    #endregion // Methods
}