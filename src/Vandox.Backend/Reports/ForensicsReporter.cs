using Microsoft.Extensions.Logging;

using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// Reads the stored data in bounded passes and writes the forensics report.
/// </summary>
public sealed class ForensicsReporter
{
    #region Constants

    /// <summary>
    /// The page size of log line reads.
    /// </summary>
    internal const int LinePage = 1000;

    /// <summary>
    /// The page size of process snapshot reads.
    /// </summary>
    internal const int SnapshotPage = 1;

    /// <summary>
    /// The page size of incident reads.
    /// </summary>
    internal const int IncidentPage = 1;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the logger; <c>null</c> for none.
    /// </summary>
    public ILogger? Logger { get; init; }

    #endregion // Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ForensicsReporter"/> class.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    /// <param name="incidents">The incident store</param>
    /// <param name="catalog">The signature catalog</param>
    /// <param name="lookback">The lookback before an outage</param>
    /// <param name="clock">The clock that stamps the report</param>
    public ForensicsReporter(IRecordReader records, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan lookback, TimeProvider clock)
    {
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Runs the passes and writes the report.
    /// </summary>
    /// <param name="writer">The writer of the report</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The counts</returns>
    /// <exception cref="StoreException">The store failed, or a page did not advance</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    public Task<ReportResult> RunAsync(MarkdownReportWriter writer, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}