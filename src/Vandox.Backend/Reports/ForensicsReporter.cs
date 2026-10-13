using Vandox.Backend.Incidents;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Reports;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Reads the stored data in bounded passes and writes the forensics report: all log lines in pages, all process snapshots one at a
/// time, then the incidents one at a time, twice.
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

    #region Fields

    private readonly IRecordReader _records;
    private readonly IIncidentStore _incidents;
    private readonly SignatureCatalog _catalog;
    private readonly TimeSpan _lookback;
    private readonly TimeProvider _clock;

    #endregion // Fields

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
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);

        _records = records;
        _incidents = incidents;
        _catalog = catalog;
        _lookback = lookback;
        _clock = clock;
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
    public async Task<ReportResult> RunAsync(MarkdownReportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var result = new ReportResult();
        var analysis = new ForensicsAnalysis(_lookback);

        await ReadLinesAsync(analysis, result, cancellationToken).ConfigureAwait(false);
        await ReadSnapshotsAsync(analysis, result, cancellationToken).ConfigureAwait(false);
        analysis.Seal();
        await PageIncidentsAsync(stored =>
                                 {
                                     analysis.AddIncident(stored.Incident);

                                     return Task.CompletedTask;
                                 },
                                 cancellationToken).ConfigureAwait(false);

        var summary = analysis.Summarize(_clock.GetUtcNow());

        await writer.WriteSummaryAsync(summary).ConfigureAwait(false);
        result.IncidentsListed = await PageIncidentsAsync(stored => writer.WriteIncidentAsync(stored, analysis.Activities(stored.Incident)), cancellationToken).ConfigureAwait(false);
        await writer.WriteNotesAsync(summary, result.IncidentsListed).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Reads all log lines in capture order: the observed hours, the line counts and the boots.
    /// </summary>
    /// <param name="analysis">The analysis</param>
    /// <param name="result">The counts</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the lines are read</returns>
    private async Task ReadLinesAsync(ForensicsAnalysis analysis, ReportResult result, CancellationToken cancellationToken)
    {
        var detector = new SignatureDetector(_catalog);
        var closed = new List<DetectedEvent>();
        var lines = new RecordQuery
                    {
                        Kind = RecordKind.LogLine,
                        From = StorableTime.Min,
                        To = StorableTime.Max
                    };

        void TakeBoots()
        {
            closed.Where(detected => detected.SignatureId == IncidentSignatures.Boot).ToList().ForEach(boot => analysis.AddBoot(boot.CapturedAt));
            closed.Clear();
        }

        await RecordPager.PageAsync(_records,
                                    lines,
                                    LinePage,
                                    record =>
                                    {
                                        result.LinesRead++;
                                        analysis.AddLogLine(record.Record.CapturedAt, record.Record.Source);
                                        detector.Add(record.Id, record.Record, closed);
                                        TakeBoots();

                                        return Task.CompletedTask;
                                    },
                                    cancellationToken)
                         .ConfigureAwait(false);
        detector.Complete(closed);
        TakeBoots();
    }

    /// <summary>
    /// Reads all process snapshots in capture order, one at a time.
    /// </summary>
    /// <param name="analysis">The analysis</param>
    /// <param name="result">The counts</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the snapshots are read</returns>
    private async Task ReadSnapshotsAsync(ForensicsAnalysis analysis, ReportResult result, CancellationToken cancellationToken)
    {
        var snapshots = new RecordQuery
                        {
                            Kind = RecordKind.ProcessSnapshot,
                            From = StorableTime.Min,
                            To = StorableTime.Max
                        };

        await RecordPager.PageAsync(_records,
                                    snapshots,
                                    SnapshotPage,
                                    record =>
                                    {
                                        if (record.Record.Data is ProcessSnapshot snapshot)
                                        {
                                            result.SnapshotsRead++;
                                            analysis.AddProcessSnapshot(record.Record.CapturedAt, snapshot);
                                        }

                                        return Task.CompletedTask;
                                    },
                                    cancellationToken)
                         .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the incidents one at a time in the order of their windows. Stored windows never overlap, so each page starts at the end
    /// of the previous incident's window; a page that does not lead past it is an error.
    /// </summary>
    /// <param name="handle">Called for every incident</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The number of incidents read</returns>
    /// <exception cref="StoreException">The store failed, or a page did not advance</exception>
    private async Task<long> PageIncidentsAsync(Func<StoredIncident, Task> handle, CancellationToken cancellationToken)
    {
        var from = StorableTime.Min;
        var count = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _incidents.IncidentsAsync(new IncidentQuery
                                                       {
                                                           From = from,
                                                           To = StorableTime.Max,
                                                           Limit = IncidentPage
                                                       },
                                                       cancellationToken)
                                       .ConfigureAwait(false);

            foreach (var stored in page)
            {
                count++;
                await handle(stored).ConfigureAwait(false);
            }

            if (page.Count < IncidentPage)
            {
                return count;
            }

            var end = page[^1].Incident.End;

            if (end <= from)
            {
                throw new StoreException("store: reading incidents: page did not advance");
            }

            if (end >= StorableTime.Max)
            {
                return count;
            }

            from = end;
        }
    }

    #endregion // Methods
}