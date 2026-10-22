using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The start or the end of a backup run.
/// </summary>
public sealed class BackupRun : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets the phase, <c>start</c> or <c>end</c>.
    /// </summary>
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ID of the boot the run happened in.
    /// </summary>
    [JsonPropertyName("boot_id")]
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the run.
    /// </summary>
    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the process ID of the first process (phase start).
    /// </summary>
    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the command of the first process (phase start).
    /// </summary>
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of matching processes at the start (phase start).
    /// </summary>
    [JsonPropertyName("processes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Processes { get; set; }

    /// <summary>
    /// Gets or sets the last time a matching process was seen (phase end).
    /// </summary>
    [JsonPropertyName("last_seen_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>
    /// Gets or sets the time by which the processes were gone (phase end).
    /// </summary>
    [JsonPropertyName("gone_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? GoneBy { get; set; }

    /// <summary>
    /// Gets or sets the most matching processes seen at once (phase end).
    /// </summary>
    [JsonPropertyName("max_processes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint MaxProcesses { get; set; }

    #endregion // Properties

    #region IPayload

    /// <inheritdoc />
    [JsonIgnore]
    public string Kind => RecordKind.BackupRun;

    /// <inheritdoc />
#pragma warning disable RH2003, S2325
    public FieldError? Validate()
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003, S2325

    #endregion // IPayload
}