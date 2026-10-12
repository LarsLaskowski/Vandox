using System.Text.Json.Serialization;

namespace Vandox.Core.Incidents;

/// <summary>
/// The memory growth of one program.
/// </summary>
public sealed class ProgramGrowth
{
    #region Properties

    /// <summary>
    /// Gets or sets the program key.
    /// </summary>
    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RSS in the first snapshot.
    /// </summary>
    [JsonPropertyName("start_rss_bytes")]
    public ulong StartRssBytes { get; set; }

    /// <summary>
    /// Gets or sets the RSS in the last snapshot.
    /// </summary>
    [JsonPropertyName("end_rss_bytes")]
    public ulong EndRssBytes { get; set; }

    /// <summary>
    /// Gets or sets the growth.
    /// </summary>
    [JsonPropertyName("growth_bytes")]
    public ulong GrowthBytes { get; set; }

    #endregion // Properties
}