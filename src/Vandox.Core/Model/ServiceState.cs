using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vandox.Core.Model;

/// <summary>
/// The state of one systemd unit.
/// </summary>
public sealed partial class ServiceState : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets the name of the unit.
    /// </summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the load state.
    /// </summary>
    [JsonPropertyName("load_state")]
    public string LoadState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the active state.
    /// </summary>
    [JsonPropertyName("active_state")]
    public string ActiveState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sub state.
    /// </summary>
    [JsonPropertyName("sub_state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SubState { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the instant the unit became active.
    /// </summary>
    [JsonPropertyName("active_enter_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ActiveEnterAt { get; set; }

    /// <summary>
    /// Gets or sets the number of restarts.
    /// </summary>
    [JsonPropertyName("restarts")]
    public uint Restarts { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the pattern of a unit name.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z0-9:_.@\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex UnitPattern();

    /// <summary>
    /// Creates the pattern of a sub state.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[a-z0-9\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SubStatePattern();

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.ServiceState;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.Pattern("unit", Unit, UnitPattern(), 256)
                        ?? Check.OneOf("load_state", LoadState, "loaded", "not-found", "bad-setting", "error", "masked", "stub", "merged")
                        ?? Check.OneOf("active_state", ActiveState, "active", "reloading", "inactive", "failed", "activating", "deactivating", "maintenance", "refreshing");

        if (error is not null)
        {
            return error;
        }

        if (SubState.Length > 0)
        {
            error = Check.Pattern("sub_state", SubState, SubStatePattern(), 64);

            if (error is not null)
            {
                return error;
            }
        }

        return Check.OptionalTime("active_enter_at", ActiveEnterAt);
    }

    #endregion // IPayload
}