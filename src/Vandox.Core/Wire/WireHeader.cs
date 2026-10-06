using System.Text.Json.Serialization;

using Vandox.Core.Model;

namespace Vandox.Core.Wire;

/// <summary>
/// The first line of a batch.
/// </summary>
public sealed class WireHeader
{
    #region Properties

    /// <summary>
    /// Gets or sets the major version of the format.
    /// </summary>
    [JsonPropertyName("format_major")]
    public int FormatMajor { get; set; }

    /// <summary>
    /// Gets or sets the minor version of the format.
    /// </summary>
    [JsonPropertyName("format_minor")]
    public int FormatMinor { get; set; }

    /// <summary>
    /// Gets or sets the ID of the agent that sent the batch.
    /// </summary>
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the boot in which every record of the batch was captured.
    /// </summary>
    [JsonPropertyName("boot_id")]
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clock offset estimate in nanoseconds, valid for the capture of every record.
    /// </summary>
    [JsonPropertyName("clock_offset_ns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ClockOffsetNs { get; set; }

    /// <summary>
    /// Gets or sets whether the batch carries current or backfilled records, one of <see cref="WireFormat.ModeLive"/> and <see cref="WireFormat.ModeBackfill"/>.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the header.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    public FieldError? Validate()
    {
        if (FormatMinor < 0)
        {
            return new FieldError("format_minor", "must not be negative");
        }

        var error = WireFormat.ValidateAgentId(AgentId);

        if (error is not null)
        {
            return error;
        }

        if (WireFormat.IsBootId(BootId))
        {
            return Mode == WireFormat.ModeLive || Mode == WireFormat.ModeBackfill ? null : new FieldError("mode", "must be live or backfill");
        }

        return new FieldError("boot_id", "must be a lower-case UUID");
    }

    #endregion // Methods
}