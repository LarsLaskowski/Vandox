using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Describes a process killed by the out-of-memory killer.
/// </summary>
public sealed class OomKill
{
    #region Properties

    /// <summary>
    /// Gets or sets the process ID of the victim.
    /// </summary>
    [JsonPropertyName("victim_pid")]
    public int VictimPid { get; set; }

    /// <summary>
    /// Gets or sets the command of the victim.
    /// </summary>
    [JsonPropertyName("victim_command")]
    public string VictimCommand { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the anonymous resident set size of the victim in bytes.
    /// </summary>
    [JsonPropertyName("anon_rss_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? AnonRssBytes { get; set; }

    /// <summary>
    /// Gets or sets the OOM score adjustment of the victim.
    /// </summary>
    [JsonPropertyName("oom_score_adj")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public short? OomScoreAdj { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the details.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate()
    {
        if (VictimPid <= 0)
        {
            return Check.Invalid("oom_kill.victim_pid", "must be greater than 0");
        }

        return Check.RequiredShort("oom_kill.victim_command", VictimCommand) ?? Check.OomScoreAdj("oom_kill.oom_score_adj", OomScoreAdj);
    }

    #endregion // Methods
}