using Vandox.Core.Incidents;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// Rebuilds the incidents from everything stored: finds the OOM kills, builds the incident of each cluster and saves it.
/// </summary>
public sealed class IncidentReconstructor
{
    #region Constants

    /// <summary>
    /// The page size of log line and metric reads.
    /// </summary>
    internal const int LinePage = 1000;

    /// <summary>
    /// The page size of metric reads.
    /// </summary>
    internal const int MetricPage = 1000;

    /// <summary>
    /// The page size of process and connection snapshot reads.
    /// </summary>
    internal const int SnapshotPage = 1;

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentReconstructor"/> class.
    /// </summary>
    /// <param name="records">Reads the records</param>
    /// <param name="templates">Reads the log templates</param>
    /// <param name="incidents">Stores the incidents</param>
    /// <param name="catalog">The signature catalog</param>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    /// <param name="clock">The clock that stamps the saves</param>
    public IncidentReconstructor(IRecordReader records, ILogTemplateReader templates, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan before, TimeSpan after, TimeProvider clock)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Rebuilds and saves the incidents.
    /// </summary>
    /// <param name="saved">Called for every saved incident</param>
    /// <param name="cancellationToken">Interrupts the run</param>
    /// <returns>A task that returns the counts</returns>
    /// <exception cref="StoreException">A read or save failed</exception>
    /// <exception cref="OperationCanceledException">The run was interrupted</exception>
    public Task<ReconstructionResult> RunAsync(Func<SavedIncident, Task> saved, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}