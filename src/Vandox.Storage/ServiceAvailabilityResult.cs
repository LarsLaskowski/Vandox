namespace Vandox.Storage;

/// <summary>
/// The availability of the systemd units of a range.
/// </summary>
public sealed class ServiceAvailabilityResult
{
    #region Properties

    /// <summary>
    /// Gets the units, at most 256, sorted by name.
    /// </summary>
    public IReadOnlyList<ServiceAvailability> Units { get; init; } = [];

    /// <summary>
    /// Gets the number of distinct units of the window; only counted when more than 256 exist.
    /// </summary>
    public int UnitCount { get; init; }

    /// <summary>
    /// Gets the time up to which the gap spans are complete, <c>null</c> when they are complete.
    /// </summary>
    public DateTimeOffset? GapsCompleteUntil { get; init; }

    /// <summary>
    /// Gets a value indicating whether a gap record names the <c>systemd</c> collector.
    /// </summary>
    public bool SystemdGaps { get; init; }

    #endregion // Properties
}