using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

/// <summary>
/// Builds the incident of a kill cluster from the records of its window.
/// </summary>
public sealed class IncidentBuilder
{
    #region Constants

    private const string BackupFirstSeen = "backup_first_seen";
    private const string BackupLastSeen = "backup_last_seen";

    #endregion // Constants

    #region Fields

    private readonly KillCluster _cluster;
    private readonly TimeSpan _before;
    private readonly TimeSpan _after;
    private readonly List<TimelineEntry> _timeline = [];
    private readonly SnapshotPeriod _period = new();
    private readonly List<MetricTracker> _metrics = CreateMetrics();
    private readonly List<IncidentNewTemplate> _newTemplates = [];
    private readonly List<IncidentTemplateSpike> _spikes = [];
    private long _timelineBytes;
    private long _timelineOmitted;
    private bool _timelineClosed;
    private DateTimeOffset? _recentBootAt;
    private long _serviceEvents;
    private bool _snapshotsStarted;
    private bool _built;
    private BackupSighting? _backupFirst;
    private BackupSighting? _backupLast;
    private long _connectionSnapshots;
    private ConnectionPeak? _peak;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentBuilder"/> class.
    /// </summary>
    /// <param name="cluster">The kill cluster</param>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    public IncidentBuilder(KillCluster cluster, TimeSpan before, TimeSpan after)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        _cluster = cluster;
        _before = before;
        _after = after;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a detected event of the window.
    /// </summary>
    /// <param name="detected">The event</param>
    /// <exception cref="InvalidOperationException">A process snapshot was added before</exception>
    public void AddEvent(DetectedEvent detected)
    {
        if (_snapshotsStarted)
        {
            throw new InvalidOperationException("incidents: events must be added before process snapshots");
        }

        if (OutsideWindow(detected.CapturedAt))
        {
            return;
        }

        if (detected.CapturedAt < _cluster.FirstKillAt)
        {
            CountLookbackEvent(detected);
        }

        AddToTimeline(detected);
    }

    /// <summary>
    /// Adds a process snapshot of the window.
    /// </summary>
    /// <param name="recordId">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    public void AddProcessSnapshot(long recordId, DateTimeOffset capturedAt, ProcessSnapshot snapshot)
    {
        _snapshotsStarted = true;

        if (OutsideWindow(capturedAt))
        {
            return;
        }

        var programs = MemoryAnalysis.Programs(snapshot);

        TrackBackup(recordId, capturedAt, programs);

        var afterBoot = _recentBootAt is null || capturedAt > _recentBootAt;

        if (afterBoot && capturedAt < _cluster.FirstKillAt)
        {
            _period.Add(capturedAt, recordId, programs);
        }
    }

    /// <summary>
    /// Adds a connection snapshot of the window.
    /// </summary>
    /// <param name="recordId">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    public void AddConnectionSnapshot(long recordId, DateTimeOffset capturedAt, ConnectionSnapshot snapshot)
    {
        if (OutsideWindow(capturedAt))
        {
            return;
        }

        _connectionSnapshots++;

        var sockets = snapshot.States?.Sum(state => (long)state.Count) ?? 0;

        if (_peak is null || sockets > _peak.Sockets || (sockets == _peak.Sockets && IsBefore(capturedAt, recordId, _peak)))
        {
            _peak = new ConnectionPeak
                    {
                        At = capturedAt,
                        RecordId = recordId,
                        Sockets = sockets,
                        Processes = TopProcesses(snapshot)
                    };
        }
    }

    /// <summary>
    /// Adds a metric sample of the window.
    /// </summary>
    /// <param name="source">The source of the record</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="metric">The metric</param>
    public void AddMetric(string source, DateTimeOffset capturedAt, MetricPoint metric)
    {
        if (OutsideWindow(capturedAt))
        {
            return;
        }

        foreach (var tracker in _metrics)
        {
            tracker.Add(source, capturedAt, metric);
        }
    }

    /// <summary>
    /// Adds a new log template of the window; only the first 20 are kept.
    /// </summary>
    /// <param name="template">The template</param>
    public void AddNewTemplate(IncidentNewTemplate template)
    {
        if (_newTemplates.Count < IncidentLimits.MaxTemplates)
        {
            _newTemplates.Add(template);
        }
    }

    /// <summary>
    /// Adds a template spike of the window; only the first 20 are kept.
    /// </summary>
    /// <param name="spike">The spike</param>
    public void AddTemplateSpike(IncidentTemplateSpike spike)
    {
        if (_spikes.Count < IncidentLimits.MaxTemplates)
        {
            _spikes.Add(spike);
        }
    }

    /// <summary>
    /// Builds the incident.
    /// </summary>
    /// <returns>The incident</returns>
    /// <exception cref="InvalidOperationException">The incident was built before</exception>
    public Incident Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("incidents: already built");
        }

        _built = true;

        var hypothesis = new IncidentHypothesis
                         {
                             RecentBootAt = _recentBootAt,
                             ServiceEvents = _serviceEvents
                         };

        _period.Fill(hypothesis);

        return new Incident
               {
                   Start = _cluster.Start,
                   End = _cluster.End,
                   WindowBeforeSeconds = (long)_before.TotalSeconds,
                   WindowAfterSeconds = (long)_after.TotalSeconds,
                   FirstKillAt = _cluster.FirstKillAt,
                   LastKillAt = _cluster.LastKillAt,
                   KillCount = _cluster.KillCount,
                   Kills = _cluster.Kills,
                   Timeline = SortedTimeline(),
                   TimelineOmitted = _timelineOmitted,
                   Hypothesis = hypothesis,
                   Metrics = _metrics.Select(tracker => tracker.ToExtreme()).OfType<MetricExtreme>().ToList(),
                   Connections = _peak is null ? null : WithSnapshotCount(_peak),
                   NewTemplates = _newTemplates,
                   TemplateSpikes = _spikes
               };
    }

    /// <summary>
    /// Creates the fixed metric definitions in their order.
    /// </summary>
    /// <returns>The trackers</returns>
    private static List<MetricTracker> CreateMetrics()
    {
        var some = new Dictionary<string, string>(StringComparer.Ordinal)
                   {
                       ["resource"] = "memory",
                       ["kind"] = "some"
                   };
        var full = new Dictionary<string, string>(StringComparer.Ordinal)
                   {
                       ["resource"] = "memory",
                       ["kind"] = "full"
                   };

        return [
                   new MetricTracker("mem_available_bytes", null, true),
                   new MetricTracker("swap_used_bytes", null, false),
                   new MetricTracker("pressure_avg10", some, false),
                   new MetricTracker("pressure_avg10", full, false),
                   new MetricTracker("load1", null, false)
               ];
    }

    /// <summary>
    /// Tells whether a connection snapshot was captured before the one that holds the peak.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="recordId">The record ID</param>
    /// <param name="peak">The peak</param>
    /// <returns><c>true</c> when the snapshot is earlier</returns>
    private static bool IsBefore(DateTimeOffset capturedAt, long recordId, ConnectionPeak peak)
    {
        return capturedAt < peak.At || (capturedAt == peak.At && recordId < peak.RecordId);
    }

    /// <summary>
    /// Returns the three processes with the most sockets, summed per command.
    /// </summary>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The processes</returns>
    private static List<ConnectionProcess> TopProcesses(ConnectionSnapshot snapshot)
    {
        return (snapshot.Processes ?? []).GroupBy(process => process.Command, StringComparer.Ordinal)
                                         .Select(group => new ConnectionProcess
                                                          {
                                                              Command = group.Key,
                                                              Count = group.Sum(process => (long)process.Count)
                                                          })
                                         .OrderByDescending(process => process.Count)
                                         .ThenBy(process => process.Command, StringComparer.Ordinal)
                                         .Take(IncidentLimits.MaxCandidates)
                                         .ToList();
    }

    /// <summary>
    /// Tells whether an instant lies outside the window.
    /// </summary>
    /// <param name="at">The instant</param>
    /// <returns><c>true</c> when it does not lie in <c>[Start, End)</c></returns>
    private bool OutsideWindow(DateTimeOffset at)
    {
        return at < _cluster.Start || at >= _cluster.End;
    }

    /// <summary>
    /// Counts a boot or service event before the first kill.
    /// </summary>
    /// <param name="detected">The event</param>
    private void CountLookbackEvent(DetectedEvent detected)
    {
        switch (detected.SignatureId)
        {
            case IncidentSignatures.Boot:
                {
                    if (_recentBootAt is null || detected.CapturedAt > _recentBootAt)
                    {
                        _recentBootAt = detected.CapturedAt;
                    }
                }
                break;

            case IncidentSignatures.ServiceStart:
            case IncidentSignatures.ServiceStop:
            case IncidentSignatures.ServiceFailed:
                {
                    _serviceEvents++;
                }
                break;
        }
    }

    /// <summary>
    /// Adds an event to the timeline unless a cap is reached; after the first event that does not fit, every later one is
    /// omitted too.
    /// </summary>
    /// <param name="detected">The event</param>
    private void AddToTimeline(DetectedEvent detected)
    {
        if (_timelineClosed)
        {
            _timelineOmitted++;

            return;
        }

        var entry = TimelineEntry.From(detected);
        var bytes = IncidentJson.Utf8Length(entry);

        if (_timeline.Count >= IncidentLimits.MaxTimelineEvents || _timelineBytes + bytes > IncidentLimits.MaxTimelineBytes)
        {
            _timelineClosed = true;
            _timelineOmitted++;

            return;
        }

        _timelineBytes += bytes;
        _timeline.Add(entry);
    }

    /// <summary>
    /// Keeps the first and the last snapshot of the window that holds a backup program.
    /// </summary>
    /// <param name="recordId">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs of the snapshot</param>
    private void TrackBackup(long recordId, DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        var found = BackupPrograms.Find(programs);

        if (found.Count == 0)
        {
            return;
        }

        var sighting = new BackupSighting(capturedAt, recordId, programs, found);

        if (_backupFirst is null || sighting.IsBefore(_backupFirst.At, _backupFirst.RecordId))
        {
            _backupFirst = sighting;
        }

        if (_backupLast is null || _backupLast.IsBefore(capturedAt, recordId))
        {
            _backupLast = sighting;
        }
    }

    /// <summary>
    /// Returns the timeline with the backup entries, ordered by time, type and first record ID.
    /// </summary>
    /// <returns>The timeline</returns>
    private List<TimelineEntry> SortedTimeline()
    {
        var entries = new List<TimelineEntry>(_timeline);

        if (_backupFirst is not null)
        {
            entries.Add(_backupFirst.ToEntry(BackupFirstSeen));
        }

        if (_backupLast is not null && _backupLast != _backupFirst)
        {
            entries.Add(_backupLast.ToEntry(BackupLastSeen));
        }

        return entries.OrderBy(entry => entry.At)
                      .ThenBy(entry => entry.Type, StringComparer.Ordinal)
                      .ThenBy(entry => entry.RecordIds.FirstOrDefault())
                      .ToList();
    }

    /// <summary>
    /// Sets the number of connection snapshots on the peak.
    /// </summary>
    /// <param name="peak">The peak</param>
    /// <returns>The peak</returns>
    private ConnectionPeak WithSnapshotCount(ConnectionPeak peak)
    {
        peak.Snapshots = _connectionSnapshots;

        return peak;
    }

    #endregion // Methods
}