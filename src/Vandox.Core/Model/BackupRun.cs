using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The start or the end of a backup run.
/// </summary>
public sealed class BackupRun : IPayload
{
    #region Constants

    /// <summary>
    /// The phase of a run that has started.
    /// </summary>
    public const string PhaseStart = "start";

    /// <summary>
    /// The phase of a run that has ended.
    /// </summary>
    public const string PhaseEnd = "end";

    private const string NotAllowedForStart = "not allowed for phase start";
    private const string NotAllowedForEnd = "not allowed for phase end";

    #endregion // Constants

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
    [OmitWhenEmpty]
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

    #region Methods

    /// <summary>
    /// Checks the fields of the phase start.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateStart()
    {
        if (LastSeenAt is not null)
        {
            return Check.Invalid("last_seen_at", NotAllowedForStart);
        }

        if (GoneBy is not null)
        {
            return Check.Invalid("gone_by", NotAllowedForStart);
        }

        if (MaxProcesses != 0)
        {
            return Check.Invalid("max_processes", NotAllowedForStart);
        }

        if (Pid <= 0)
        {
            return Check.Invalid("pid", "must be greater than 0");
        }

        var error = Check.RequiredShort("command", Command);

        if (error is not null)
        {
            return error;
        }

        return Processes < 1 ? Check.Invalid("processes", "must be at least 1") : null;
    }

    /// <summary>
    /// Checks the fields of the phase end.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateEnd()
    {
        if (Pid != 0)
        {
            return Check.Invalid("pid", NotAllowedForEnd);
        }

        if (Command.Length != 0)
        {
            return Check.Invalid("command", NotAllowedForEnd);
        }

        if (Processes != 0)
        {
            return Check.Invalid("processes", NotAllowedForEnd);
        }

        var lastSeen = LastSeenAt.GetValueOrDefault();
        var error = Check.Time("last_seen_at", lastSeen);

        if (error is not null)
        {
            return error;
        }

        if (lastSeen < StartedAt)
        {
            return Check.Invalid("last_seen_at", "must not be before started_at");
        }

        var goneBy = GoneBy.GetValueOrDefault();

        error = Check.Time("gone_by", goneBy);

        if (error is not null)
        {
            return error;
        }

        if (goneBy < lastSeen)
        {
            return Check.Invalid("gone_by", "must not be before last_seen_at");
        }

        return MaxProcesses < 1 ? Check.Invalid("max_processes", "must be at least 1") : null;
    }

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    [JsonIgnore]
    public string Kind => RecordKind.BackupRun;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.OneOf("phase", Phase, PhaseStart, PhaseEnd)
                        ?? Check.Uuid("boot_id", BootId)
                        ?? Check.Time("started_at", StartedAt);

        if (error is not null)
        {
            return error;
        }

        return Phase == PhaseStart ? ValidateStart() : ValidateEnd();
    }

    #endregion // IPayload
}