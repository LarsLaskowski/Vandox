namespace Vandox.Storage;

/// <summary>
/// The availability of one systemd unit in a range.
/// </summary>
public sealed class ServiceAvailability
{
    #region Properties

    /// <summary>
    /// Gets the unit name.
    /// </summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// Gets the active state of the latest record of the unit.
    /// </summary>
    public string ActiveState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the sub state of the latest record of the unit.
    /// </summary>
    public string SubState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the time the unit was available.
    /// </summary>
    public TimeSpan Available { get; init; }

    /// <summary>
    /// Gets the time the unit was not available.
    /// </summary>
    public TimeSpan NotAvailable { get; init; }

    /// <summary>
    /// Gets the time the state of the unit is unknown.
    /// </summary>
    public TimeSpan Unknown { get; init; }

    /// <summary>
    /// Gets the capture time of the latest record of the unit, <c>null</c> when none was read.
    /// </summary>
    public DateTimeOffset? StateAt { get; init; }

    #endregion // Properties
}