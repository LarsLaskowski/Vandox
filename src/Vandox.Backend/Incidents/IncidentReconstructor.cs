using System.Globalization;

using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Rebuilds the incidents from everything stored: finds the OOM kills, builds the incident of each cluster and saves it.
/// </summary>
public sealed class IncidentReconstructor
{
    #region Constants

    /// <summary>
    /// The page size of log line reads.
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

    #region Fields

    private readonly IRecordReader _records;
    private readonly ILogTemplateReader _templates;
    private readonly IIncidentStore _incidents;
    private readonly SignatureCatalog _catalog;
    private readonly TimeSpan _before;
    private readonly TimeSpan _after;
    private readonly TimeProvider _clock;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the logger that reports skipped incidents; <c>null</c> for none.
    /// </summary>
    public ILogger? Logger { get; init; }

    #endregion // Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentReconstructor"/> class.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    /// <param name="templates">The reader of log templates</param>
    /// <param name="incidents">The incident store</param>
    /// <param name="catalog">The signature catalog</param>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    /// <param name="clock">The clock that stamps the saved incidents</param>
    public IncidentReconstructor(IRecordReader records, ILogTemplateReader templates, IIncidentStore incidents, SignatureCatalog catalog, TimeSpan before, TimeSpan after, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(clock);

        _records = records;
        _templates = templates;
        _incidents = incidents;
        _catalog = catalog;
        _before = before;
        _after = after;
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Rebuilds the incidents.
    /// </summary>
    /// <param name="saved">Called with every incident that was saved, in time order</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The counts</returns>
    /// <exception cref="StoreException">The store failed, or a page of records did not advance</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    public async Task<ReconstructionResult> RunAsync(Func<SavedIncident, Task> saved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(saved);

        var result = new ReconstructionResult();
        var clusterer = new KillClusterer(_before, _after);
        var detector = new SignatureDetector(_catalog);
        var closed = new List<DetectedEvent>();
        var lines = new RecordQuery
                    {
                        Kind = RecordKind.LogLine,
                        From = StorableTime.Min,
                        To = StorableTime.Max
                    };

        async Task OnClusterAsync(KillCluster? cluster)
        {
            if (cluster is not null)
            {
                await ProcessAsync(cluster, saved, result, cancellationToken).ConfigureAwait(false);
            }
        }

        async Task DrainAsync()
        {
            foreach (var detected in closed.Where(item => item.SignatureId == IncidentSignatures.OomKill))
            {
                result.Kills++;
                await OnClusterAsync(clusterer.Add(detected)).ConfigureAwait(false);
            }

            closed.Clear();
        }

        await PageAsync(lines,
                        LinePage,
                        async record =>
                        {
                            result.LinesRead++;
                            detector.Add(record.Id, record.Record, closed);
                            await DrainAsync().ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
        detector.Complete(closed);
        await DrainAsync().ConfigureAwait(false);
        await OnClusterAsync(clusterer.Complete()).ConfigureAwait(false);

        var stored = await _incidents.CountIncidentsAsync(cancellationToken).ConfigureAwait(false);

        result.Kept = stored - (result.Created + result.Updated + result.Unchanged);

        return result;
    }

    /// <summary>
    /// Counts the outcome of a save.
    /// </summary>
    /// <param name="result">The counts</param>
    /// <param name="outcome">The save result</param>
    private static void Count(ReconstructionResult result, IncidentSaveResult outcome)
    {
        result.Merged += outcome.Merged;

        switch (outcome.Outcome)
        {
            case IncidentSaveOutcome.Created:
                result.Created++;

                break;

            case IncidentSaveOutcome.Updated:
                result.Updated++;

                break;

            default:
                result.Unchanged++;

                break;
        }
    }

    /// <summary>
    /// Creates the range query of a kind over the window of a cluster.
    /// </summary>
    /// <param name="kind">The record kind</param>
    /// <param name="cluster">The cluster</param>
    /// <returns>The query without paging fields</returns>
    private static RecordQuery Range(string kind, KillCluster cluster)
    {
        return new RecordQuery
               {
                   Kind = kind,
                   From = cluster.Start,
                   To = cluster.End
               };
    }

    /// <summary>
    /// Reads a range page by page, by capture time and record ID. A full page must lead to a position after the previous one.
    /// </summary>
    /// <param name="range">The kind and the range</param>
    /// <param name="limit">The page size</param>
    /// <param name="handle">Called for every record</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the range is read</returns>
    private async Task PageAsync(RecordQuery range, int limit, Func<StoredRecord, Task> handle, CancellationToken cancellationToken)
    {
        var from = range.From;
        var after = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _records.RecordsAsync(new RecordQuery
                                                   {
                                                       Kind = range.Kind,
                                                       From = from,
                                                       To = range.To,
                                                       Limit = limit,
                                                       AfterId = after
                                                   },
                                                   cancellationToken)
                                     .ConfigureAwait(false);

            foreach (var record in page)
            {
                await handle(record).ConfigureAwait(false);
            }

            if (page.Count < limit)
            {
                return;
            }

            var last = page[^1];

            if (last.Record.CapturedAt < from || (last.Record.CapturedAt == from && last.Id <= after))
            {
                throw new StoreException("store: reading records: page did not advance");
            }

            from = last.Record.CapturedAt;
            after = last.Id;
        }
    }

    /// <summary>
    /// Builds and saves the incident of a cluster.
    /// </summary>
    /// <param name="cluster">The cluster</param>
    /// <param name="saved">Called with the saved incident</param>
    /// <param name="result">The counts</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the incident is saved or skipped</returns>
    private async Task ProcessAsync(KillCluster cluster, Func<SavedIncident, Task> saved, ReconstructionResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var builder = new IncidentBuilder(cluster, _before, _after);

        await ReadWindowAsync(builder, cluster, cancellationToken).ConfigureAwait(false);
        await AddTemplatesAsync(builder, cluster, cancellationToken).ConfigureAwait(false);

        var incident = builder.Build();

        result.Incidents++;

        try
        {
            var outcome = await _incidents.SaveIncidentAsync(incident, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

            Count(result, outcome);
            await saved(new SavedIncident
                        {
                            Result = outcome,
                            Incident = incident
                        }).ConfigureAwait(false);
        }
        catch (InvalidIncidentException exception)
        {
            result.Skipped++;
            Logger?.IncidentSkipped(incident.FirstKillAt.ToString("O", CultureInfo.InvariantCulture), exception.Message);
        }
    }

    /// <summary>
    /// Reads the log lines, the process and connection snapshots and the metrics of the window into the builder. The lines
    /// come first because the builder judges the snapshots by the last boot.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="cluster">The cluster whose window is read</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the window is read</returns>
    private async Task ReadWindowAsync(IncidentBuilder builder, KillCluster cluster, CancellationToken cancellationToken)
    {
        var detector = new SignatureDetector(_catalog);
        var closed = new List<DetectedEvent>();

        await PageAsync(Range(RecordKind.LogLine, cluster),
                        LinePage,
                        record =>
                        {
                            detector.Add(record.Id, record.Record, closed);
                            closed.ForEach(builder.AddEvent);
                            closed.Clear();

                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
        detector.Complete(closed);
        closed.ForEach(builder.AddEvent);

        await PageAsync(Range(RecordKind.ProcessSnapshot, cluster),
                        SnapshotPage,
                        record =>
                        {
                            if (record.Record.Data is ProcessSnapshot snapshot)
                            {
                                builder.AddProcessSnapshot(record.Id, record.Record.CapturedAt, snapshot);
                            }

                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
        await PageAsync(Range(RecordKind.ConnectionSnapshot, cluster),
                        SnapshotPage,
                        record =>
                        {
                            if (record.Record.Data is ConnectionSnapshot snapshot)
                            {
                                builder.AddConnectionSnapshot(record.Id, record.Record.CapturedAt, snapshot);
                            }

                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
        await PageAsync(Range(RecordKind.Metric, cluster),
                        MetricPage,
                        record =>
                        {
                            if (record.Record.Data is MetricPoint metric)
                            {
                                builder.AddMetric(record.Record.Source, record.Record.CapturedAt, metric);
                            }

                            return Task.CompletedTask;
                        },
                        cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the new templates and the template spikes of the window into the builder.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="cluster">The cluster whose window is read</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the templates are read</returns>
    private async Task AddTemplatesAsync(IncidentBuilder builder, KillCluster cluster, CancellationToken cancellationToken)
    {
        var created = await _templates.NewTemplatesAsync(new NewTemplateQuery
                                                         {
                                                             From = cluster.Start,
                                                             To = cluster.End,
                                                             Limit = IncidentLimits.MaxTemplates
                                                         },
                                                         cancellationToken)
                                      .ConfigureAwait(false);

        foreach (var template in created)
        {
            builder.AddNewTemplate(new IncidentNewTemplate
                                   {
                                       Source = template.Source,
                                       Program = template.Program,
                                       Template = template.Template,
                                       FirstSeen = template.FirstSeen,
                                       Count = template.Count
                                   });
        }

        var limit = cluster.Start.AddHours(StorageLimits.MaxSpikeRangeHours);
        var spikes = await _templates.TemplateSpikesAsync(new TemplateSpikeQuery
                                                          {
                                                              From = cluster.Start,
                                                              To = cluster.End < limit ? cluster.End : limit,
                                                              Limit = IncidentLimits.MaxTemplates
                                                          },
                                                          cancellationToken)
                                     .ConfigureAwait(false);

        foreach (var spike in spikes)
        {
            builder.AddTemplateSpike(new IncidentTemplateSpike
                                     {
                                         Source = spike.Template.Source,
                                         Program = spike.Template.Program,
                                         Template = spike.Template.Template,
                                         Hour = spike.Hour,
                                         Count = spike.Count,
                                         UsualLevel = spike.UsualLevel
                                     });
        }
    }

    #endregion // Methods
}