using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The connections of one process command.
/// </summary>
public sealed class ConnectionProcess
{
    #region Properties

    /// <summary>
    /// Gets or sets the command.
    /// </summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of connections.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    #endregion // Properties
}