using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Describes a system boot.
/// </summary>
public sealed class Boot
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID of the boot.
    /// </summary>
    [JsonPropertyName("boot_id")]
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the ID of the previous boot.
    /// </summary>
    [JsonPropertyName("previous_boot_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string PreviousBootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant of the boot.
    /// </summary>
    [JsonPropertyName("booted_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? BootedAt { get; set; }

    /// <summary>
    /// Gets or sets the uptime of the previous boot in nanoseconds.
    /// </summary>
    [JsonPropertyName("previous_uptime_ns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PreviousUptimeNs { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the details.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate()
    {
        var error = Check.Uuid("boot.boot_id", BootId)
                        ?? Check.OptionalUuid("boot.previous_boot_id", PreviousBootId)
                        ?? Check.OptionalTime("boot.booted_at", BootedAt);

        if (error is not null)
        {
            return error;
        }

        return PreviousUptimeNs < 0 ? Check.Invalid("boot.previous_uptime_ns", "must not be negative") : null;
    }

    #endregion // Methods
}