namespace Vandox.Storage;

/// <summary>
/// Reads the rollups of connection snapshots.
/// </summary>
public interface IConnectionRollupReader
{
    #region Methods

    /// <summary>
    /// Returns the states and the top programs of one source over a range, per step.
    /// </summary>
    /// <param name="query">The source, range and step</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the lines of the states and programs</returns>
    Task<ConnectionRollupResult> ConnectionRollupsAsync(ConnectionRollupQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the top remote addresses of one source over a range.
    /// </summary>
    /// <param name="query">The source, range and step</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the addresses with the figures per step</returns>
    Task<ConnectionAddressResult> ConnectionAddressesAsync(ConnectionRollupQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the sources that have connection rollups in a range.
    /// </summary>
    /// <param name="query">The range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the sources</returns>
    Task<ProgramSourceList> ConnectionSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}