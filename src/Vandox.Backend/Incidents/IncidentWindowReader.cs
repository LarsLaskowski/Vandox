using Vandox.Backend.Components.Charts;
using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// Reads the data of the charts of an incident window from the stored records.
/// </summary>
public sealed class IncidentWindowReader
{
    #region Constants

    private const int SnapshotPage = 1;

    private const double BytesPerMib = 1048576.0;

    private const string AvailableMemoryMetric = "mem_available_bytes";

    private const string SwapMetric = "swap_used_bytes";

    #endregion // Constants

    #region Fields

    private readonly IRecordReader _records;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentWindowReader"/> class.
    /// </summary>
    /// <param name="records">The reader of stored records</param>
    public IncidentWindowReader(IRecordReader records)
    {
        ArgumentNullException.ThrowIfNull(records);

        _records = records;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the memory per program, the available memory and swap, and the connections of the window of an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the data of the charts</returns>
    /// <exception cref="StoreException">The store failed</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled</exception>
    public async Task<IncidentWindowData> ReadAsync(Incident incident, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var memorySource = await FindSourceAsync(incident, RecordKind.ProcessSnapshot, cancellationToken).ConfigureAwait(false);
        var memory = new ProgramMemorySeries();
        var memoryLast = DateTimeOffset.MinValue;
        var memoryCut = await ReadSnapshotsAsync(incident,
                                                 RecordKind.ProcessSnapshot,
                                                 memorySource,
                                                 IncidentsView.MaxChartSnapshots,
                                                 record =>
                                                 {
                                                     if (record.Record.Data is ProcessSnapshot snapshot && memory.Add(record.Record.CapturedAt, MemoryAnalysis.Programs(snapshot)))
                                                     {
                                                         memoryLast = record.Record.CapturedAt;
                                                     }
                                                 },
                                                 cancellationToken).ConfigureAwait(false);
        var memoryAndSwap = await ReadMemoryAndSwapAsync(incident, cancellationToken).ConfigureAwait(false);
        var connectionsSource = await FindSourceAsync(incident, RecordKind.ConnectionSnapshot, cancellationToken).ConfigureAwait(false);
        var times = new List<DateTimeOffset>();
        var sockets = new List<double?>();
        var connectionsCut = await ReadSnapshotsAsync(incident,
                                                      RecordKind.ConnectionSnapshot,
                                                      connectionsSource,
                                                      IncidentsView.MaxChartConnections,
                                                      record =>
                                                      {
                                                          if (record.Record.Data is ConnectionSnapshot snapshot && (times.Count == 0 || record.Record.CapturedAt > times[^1]))
                                                          {
                                                              times.Add(record.Record.CapturedAt);
                                                              sockets.Add((snapshot.States ?? []).Sum(state => (double)state.Count));
                                                          }
                                                      },
                                                      cancellationToken).ConfigureAwait(false);

        return new IncidentWindowData
               {
                   MemorySource = memory.Count > 0 ? memorySource : string.Empty,
                   ConnectionsSource = times.Count > 0 ? connectionsSource : string.Empty,
                   Memory = memory.ToChart(),
                   MemoryAndSwap = memoryAndSwap,
                   Connections = times.Count > 0 ? new ChartData(times, [new ChartSeries(IncidentsView.Sockets, sockets)]) : null,
                   MemoryCutAt = memoryCut && memory.Count > 0 ? memoryLast : null,
                   ConnectionsCutAt = connectionsCut && times.Count > 0 ? times[^1] : null
               };
    }

    /// <summary>
    /// Finds the source of the earliest snapshot of a kind in the window.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="kind">The kind of the snapshots</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the source, or an empty text without a snapshot</returns>
    private async Task<string> FindSourceAsync(Incident incident, string kind, CancellationToken cancellationToken)
    {
        var first = await _records.RecordsAsync(new RecordQuery
                                                {
                                                    Kind = kind,
                                                    From = incident.Start,
                                                    To = incident.End,
                                                    Limit = 1
                                                },
                                                cancellationToken)
                                  .ConfigureAwait(false);

        return first.Count > 0 ? first[0].Record.Source : string.Empty;
    }

    /// <summary>
    /// Reads the snapshots of a kind and a source in the window, one per page, and hands the first ones to a callback.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="kind">The kind of the snapshots</param>
    /// <param name="source">The source of the snapshots; an empty text reads nothing</param>
    /// <param name="cap">The most snapshots handed to the callback</param>
    /// <param name="add">Called for each of the first snapshots</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns whether a snapshot beyond the cap exists</returns>
    private async Task<bool> ReadSnapshotsAsync(Incident incident, string kind, string source, int cap, Action<StoredRecord> add, CancellationToken cancellationToken)
    {
        if (source.Length == 0)
        {
            return false;
        }

        var seen = 0;
        var count = await RecordPager.PageAsync(_records,
                                                new RecordQuery
                                                {
                                                    Kind = kind,
                                                    Source = source,
                                                    From = incident.Start,
                                                    To = incident.End
                                                },
                                                SnapshotPage,
                                                cap + 1,
                                                record =>
                                                {
                                                    seen++;

                                                    if (seen <= cap)
                                                    {
                                                        add(record);
                                                    }

                                                    return Task.CompletedTask;
                                                },
                                                cancellationToken)
                                     .ConfigureAwait(false);

        return count > cap;
    }

    /// <summary>
    /// Reads the available memory and the swap of the window from the sources of the stored extremes of the incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the chart, or <c>null</c> without a point</returns>
    private async Task<ChartData?> ReadMemoryAndSwapAsync(Incident incident, CancellationToken cancellationToken)
    {
        var available = await ReadMetricAsync(incident, AvailableMemoryMetric, cancellationToken).ConfigureAwait(false);
        var swap = await ReadMetricAsync(incident, SwapMetric, cancellationToken).ConfigureAwait(false);
        var series = new List<(string Label, Dictionary<DateTimeOffset, double?> Points)>();

        if (available.Count > 0)
        {
            series.Add((IncidentsView.AvailableMemory, available));
        }

        if (swap.Count > 0)
        {
            series.Add((IncidentsView.SwapUsed, swap));
        }

        if (series.Count == 0)
        {
            return null;
        }

        var times = series.SelectMany(item => item.Points.Keys).Distinct().Order().ToList();

        return new ChartData(times, [.. series.Select(item => new ChartSeries(item.Label, [.. times.Select(time => item.Points.GetValueOrDefault(time))]))]);
    }

    /// <summary>
    /// Reads the points of a metric in the window from the source of its stored extreme.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="name">The name of the metric</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that returns the value in MiB by time, <c>null</c> for a value that is not finite; empty without a stored extreme</returns>
    private async Task<Dictionary<DateTimeOffset, double?>> ReadMetricAsync(Incident incident, string name, CancellationToken cancellationToken)
    {
        var points = new Dictionary<DateTimeOffset, double?>();
        var extreme = incident.Metrics.Find(metric => metric.Name == name);

        if (extreme is null)
        {
            return points;
        }

        var page = await _records.RecordsAsync(new RecordQuery
                                               {
                                                   Kind = RecordKind.Metric,
                                                   Source = extreme.Source,
                                                   Name = name,
                                                   From = incident.Start,
                                                   To = incident.End,
                                                   Limit = IncidentsView.MaxMetricPoints
                                               },
                                               cancellationToken)
                                 .ConfigureAwait(false);

        foreach (var record in page.Select(stored => stored.Record))
        {
            if (record.Data is MetricPoint metric)
            {
                points[record.CapturedAt] = double.IsFinite(metric.Value) ? metric.Value / BytesPerMib : null;
            }
        }

        return points;
    }

    #endregion // Methods
}