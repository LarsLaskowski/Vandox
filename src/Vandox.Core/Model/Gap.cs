using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Records a period without data.
/// </summary>
public sealed class Gap : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets the start of the period.
    /// </summary>
    [JsonPropertyName("from")]
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// Gets or sets the end of the period.
    /// </summary>
    [JsonPropertyName("to")]
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Gets or sets why data is missing, one of the <see cref="GapCause"/> constants.
    /// </summary>
    [JsonPropertyName("cause")]
    public string Cause { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the collector that timed out.
    /// </summary>
    [JsonPropertyName("collector")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Collector { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first missing sequence number.
    /// </summary>
    [JsonPropertyName("first_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong FirstSeq { get; set; }

    /// <summary>
    /// Gets or sets the last missing sequence number.
    /// </summary>
    [JsonPropertyName("last_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong LastSeq { get; set; }

    #endregion // Properties

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.Gap;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.Time("from", From) ?? Check.Time("to", To);

        if (error is not null)
        {
            return error;
        }

        if (To <= From)
        {
            return Check.Invalid("to", "must be after from");
        }

        error = Check.OneOf("cause", Cause, GapCause.AgentNotRunning, GapCause.SpoolDropped, GapCause.CollectorTimeout, GapCause.SequenceMissing, GapCause.NoData, GapCause.Unknown)
                    ?? Check.OptionalName("collector", Collector);

        if (error is not null)
        {
            return error;
        }

        if (Cause == GapCause.CollectorTimeout && Collector.Length == 0)
        {
            return Check.Invalid("collector", "required for cause collector_timeout");
        }

        var firstSet = FirstSeq != 0;
        var lastSet = LastSeq != 0;

        if (firstSet != lastSet || FirstSeq > LastSeq)
        {
            return Check.Invalid("last_seq", "first_seq and last_seq must both be set, first_seq not after last_seq");
        }

        if (FirstSeq == 0 && (Cause == GapCause.SpoolDropped || Cause == GapCause.SequenceMissing))
        {
            return Check.Invalid("first_seq", "required for this cause");
        }

        return null;
    }

    #endregion // IPayload
}