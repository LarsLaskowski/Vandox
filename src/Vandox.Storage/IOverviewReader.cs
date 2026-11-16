namespace Vandox.Storage;

/// <summary>
/// Reads the latest values the overview page shows.
/// </summary>
public interface IOverviewReader
{
    #region Methods

    /// <summary>
    /// Reads how fresh the agent's data is.
    /// </summary>
    /// <param name="liveScanLimit">The number of newest metric records to scan for a live one</param>
    /// <param name="until">The latest capture time that counts</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the freshness</returns>
    Task<AgentFreshness> FreshnessAsync(int liveScanLimit, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the newest values of several series.
    /// </summary>
    /// <param name="query">The series, window and limit</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the newest values per series</returns>
    Task<IReadOnlyList<LatestSeries>> LatestValuesAsync(LatestValueQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the newest MariaDB status record.
    /// </summary>
    /// <param name="until">The latest capture time that counts</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the status, or null when there is none</returns>
    Task<MariaDbLatest?> LatestMariaDbStatusAsync(DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the newest state of each current service unit.
    /// </summary>
    /// <param name="query">The retirement, limit and latest time</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns a page of states</returns>
    Task<ServiceStatePage> LatestServiceStatesAsync(LatestServiceQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}