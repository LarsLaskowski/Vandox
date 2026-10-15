using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Runs the forensics report for the web: one run at a time across the service, through the gated readers.
/// </summary>
public sealed class ReportService : IDisposable
{
    #region Fields

    private readonly IRecordReader _records;

    private readonly IIncidentStore _incidents;

    private readonly SignatureCatalog _catalog;

    private readonly TimeSpan _lookback;

    private readonly TimeProvider _clock;

    private readonly SemaphoreSlim _slot = new(1, 1);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportService"/> class.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    /// <param name="incidents">The incident store</param>
    /// <param name="catalog">The signature catalog</param>
    /// <param name="lookback">The lookback before an outage</param>
    /// <param name="clock">The clock that stamps the report</param>
    public ReportService(IRecordReader records, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock)
    {
        _records = records;
        _incidents = incidents;
        _catalog = catalog;
        _lookback = lookback;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Waits for the run slot, then runs the report into the writer.
    /// </summary>
    /// <param name="writer">The receiver of the report</param>
    /// <param name="cancellationToken">Ends the wait for the slot and the run</param>
    /// <returns>A task that returns the result of the run</returns>
    public Task<ReportResult> RunAsync(IReportWriter writer, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _slot.Dispose();
    }

    #endregion // IDisposable
}