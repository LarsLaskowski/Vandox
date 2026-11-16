namespace Vandox.Storage;

/// <summary>
/// A page of the newest service states.
/// </summary>
public sealed class ServiceStatePage
{
    #region Properties

    /// <summary>
    /// Gets the states, not active ones first.
    /// </summary>
    public IReadOnlyList<LatestServiceState> States { get; init; } = [];

    /// <summary>
    /// Gets the number of current units.
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Gets the capture time of the newest state, if any.
    /// </summary>
    public DateTimeOffset? Newest { get; init; }

    #endregion // Properties
}