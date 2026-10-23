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

    /// <summary>
    /// Gets or sets the ID of the boot the kill happened in, empty when absent.
    /// </summary>
    [JsonPropertyName("boot_id")]
    [OmitWhenEmpty]
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kernel clock reading of the kill in nanoseconds.
    /// </summary>
    [JsonPropertyName("kernel_time_ns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? KernelTimeNs { get; set; }

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

        var error = Check.RequiredShort("oom_kill.victim_command", VictimCommand)
                        ?? Check.OomScoreAdj("oom_kill.oom_score_adj", OomScoreAdj)
                        ?? Check.OptionalUuid("oom_kill.boot_id", BootId);

        if (error is not null)
        {
            return error;
        }

        return KernelTimeNs < 0 ? Check.Invalid("oom_kill.kernel_time_ns", "must not be negative") : null;
    }

    #endregion // Methods
}