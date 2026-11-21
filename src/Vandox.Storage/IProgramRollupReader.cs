namespace Vandox.Storage;

/// <summary>
/// Reads the per-program rollups of process snapshots.
/// </summary>
public interface IProgramRollupReader
{
    #region Methods

    /// <summary>
    /// Returns the top programs of one source over a range, per step.
    /// </summary>
    /// <param name="query">The source, range, step, measure and number of programs</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the lines of the programs</returns>
    Task<ProgramRollupResult> ProgramRollupsAsync(ProgramRollupQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the sources that have program rollups in a range.
    /// </summary>
    /// <param name="query">The range</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the sources</returns>
    Task<ProgramSourceList> ProgramSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken);

    #endregion // Methods
}