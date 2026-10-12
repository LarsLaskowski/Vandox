using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The memory use of one program.
/// </summary>
public sealed class ProgramUsage
{
    #region Properties

    /// <summary>
    /// Gets or sets the program key.
    /// </summary>
    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the summed RSS.
    /// </summary>
    [JsonPropertyName("rss_bytes")]
    public ulong RssBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of processes.
    /// </summary>
    [JsonPropertyName("count")]
    public long Count { get; set; }

    #endregion // Properties
}