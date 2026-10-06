namespace Vandox.Core.Model;

/// <summary>
/// One captured fact: metadata plus a payload.
/// </summary>
public sealed class DataRecord
{
    #region Properties

    /// <summary>
    /// Gets or sets who produced the record, one of the <see cref="RecordOrigin"/> constants.
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the source (log, collector, host).
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sequence number of an agent record; 0 for every other origin.
    /// </summary>
    public ulong Seq { get; set; }

    /// <summary>
    /// Gets or sets the instant the fact was captured, in UTC.
    /// </summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the payload.
    /// </summary>
    public IPayload? Data { get; set; }

    /// <summary>
    /// Gets the kind of the payload, or an empty text when there is none.
    /// </summary>
    public string Kind => Data is null ? string.Empty : Data.Kind;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the metadata and the payload.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    public FieldError? Validate()
    {
        var error = ValidateMeta();

        if (error is not null)
        {
            return error;
        }

        if (Data is null)
        {
            return Check.Invalid("data", "required");
        }

        error = Data.Validate();

        if (error is not null)
        {
            return error.WithPrefix("data");
        }

        if (Data is Gap gap
            && Origin == RecordOrigin.Agent
            && (gap.Cause == GapCause.SequenceMissing || gap.Cause == GapCause.NoData))
        {
            return Check.Invalid("data.cause", "not allowed for origin agent");
        }

        return null;
    }

    /// <summary>
    /// Checks the metadata.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateMeta()
    {
        if (Origin != RecordOrigin.Agent && Origin != RecordOrigin.Import && Origin != RecordOrigin.Backend)
        {
            return Check.Invalid("origin", "unknown origin");
        }

        var error = Check.Name("source", Source) ?? Check.Time("captured_at", CapturedAt);

        if (error is not null)
        {
            return error;
        }

        if (Origin == RecordOrigin.Agent && Seq == 0)
        {
            return Check.Invalid("seq", "must be greater than 0 for origin agent");
        }

        if (Origin != RecordOrigin.Agent && Seq != 0)
        {
            return Check.Invalid("seq", "must be 0 unless origin is agent");
        }

        return null;
    }

    #endregion // Methods
}