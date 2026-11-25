namespace Vandox.Storage;

/// <summary>
/// Reads the availability of the systemd units for the services view.
/// </summary>
public interface IServiceHistoryReader
{
    #region Methods

    /// <summary>
    /// Reads the availability of the units of a range: the time each recorded state holds up to its cap, minus the time of the gaps of the <c>systemd</c> collector.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The units, at most 256, and the facts about the gaps</returns>
    /// <exception cref="InvalidQueryException">The query breaks a rule; nothing was read</exception>
    /// <exception cref="StoreException">The read fails</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled</exception>
    Task<ServiceAvailabilityResult> ServiceAvailabilityAsync(ServiceAvailabilityQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}