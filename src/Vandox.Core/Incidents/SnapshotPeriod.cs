namespace Vandox.Core.Incidents;

/// <summary>
/// Tracks the process snapshots of the snapshot period: the first and the last one and the backup programs seen.
/// </summary>
internal sealed class SnapshotPeriod
{
    #region Fields

    private readonly HashSet<string> _backups = new(StringComparer.Ordinal);
    private DateTimeOffset _firstAt;
    private long _firstId;
    private IReadOnlyDictionary<string, ProgramUsage>? _first;
    private DateTimeOffset _lastAt;
    private long _lastId;
    private IReadOnlyDictionary<string, ProgramUsage>? _last;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of snapshots.
    /// </summary>
    internal long Count { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot of the period.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="recordId">The record ID</param>
    /// <param name="programs">The programs of the snapshot</param>
    internal void Add(DateTimeOffset at, long recordId, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        Count++;

        foreach (var name in BackupPrograms.Find(programs))
        {
            _backups.Add(name);
        }

        if (_first is null || at < _firstAt || (at == _firstAt && recordId < _firstId))
        {
            _first = programs;
            _firstAt = at;
            _firstId = recordId;
        }

        if (_last is null || at > _lastAt || (at == _lastAt && recordId > _lastId))
        {
            _last = programs;
            _lastAt = at;
            _lastId = recordId;
        }
    }

    /// <summary>
    /// Writes the hypothesis fields that depend on the snapshots.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    internal void Fill(IncidentHypothesis hypothesis)
    {
        hypothesis.SnapshotCount = Count;

        if (_first is null || _last is null)
        {
            return;
        }

        hypothesis.FirstSnapshotAt = _firstAt;
        hypothesis.LastSnapshotAt = _lastAt;
        hypothesis.GrowthKnown = Count >= 2;
        hypothesis.Consumers = MemoryAnalysis.Largest(_last, IncidentLimits.MaxCandidates).ToList();
        hypothesis.BackupRunning = _backups.Count > 0;
        hypothesis.BackupPrograms = BackupPrograms.Names.Where(_backups.Contains).ToList();

        if (hypothesis.GrowthKnown)
        {
            hypothesis.Growth = MemoryAnalysis.Growth(_first, _last, IncidentLimits.MaxCandidates).ToList();
        }
    }

    #endregion // Methods
}