using Vandox.Core.Incidents;
using Vandox.Core.Model;

namespace Vandox.Core.Reports;

/// <summary>
/// The forensics analysis over the whole period. Use order: <c>AddLogLine</c>, <c>AddBoot</c> and <c>AddProcessSnapshot</c>, then
/// <see cref="Seal"/>, then <see cref="AddIncident"/> for each incident, then <see cref="Summarize"/>; a call out of order throws
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class ForensicsAnalysis
{
    #region Constants

    private const int HoursPerDay = 24;
    private const int ActivityCount = 3;
    private const int BackupIndex = 0;
    private const int BootIndex = 1;
    private const int PanelIndex = 2;

    private static readonly string[] _systemLogSources = ["journal", "syslog"];

    #endregion // Constants

    #region Fields

    private readonly TimeSpan _lookback;
    private readonly int _maxBoots;
    private readonly ObservedHours _observed;
    private readonly SnapshotSeries _series;
    private readonly ConsumerTracker _consumers;
    private readonly List<long> _boots = [];
    private readonly long[] _hourOutages = new long[HoursPerDay];
    private readonly long[] _outagesWith = new long[ActivityCount];
    private readonly long[] _outagesKnown = new long[ActivityCount];
    private readonly long[] _hoursWith = new long[ActivityCount];
    private readonly long[] _hoursKnown = new long[ActivityCount];
    private bool _sealed;
    private bool _bootsTruncated;
    private ulong _panelThreshold;
    private long _logLines;
    private DateTimeOffset? _firstLogLineAt;
    private DateTimeOffset? _lastLogLineAt;
    private long _snapshots;
    private DateTimeOffset? _firstSnapshotAt;
    private DateTimeOffset? _lastSnapshotAt;
    private long _incidents;
    private long _kills;
    private DateTimeOffset? _firstOutageAt;
    private DateTimeOffset? _lastOutageAt;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ForensicsAnalysis"/> class with the default caps.
    /// </summary>
    /// <param name="lookback">The lookback before an outage</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lookback"/> is not positive</exception>
    public ForensicsAnalysis(TimeSpan lookback)
        : this(lookback, ReportLimits.MaxSnapshots, ReportLimits.MaxBoots, ReportLimits.MaxObservedRuns, ReportLimits.MaxPrograms)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ForensicsAnalysis"/> class.
    /// </summary>
    /// <param name="lookback">The lookback before an outage</param>
    /// <param name="maxSnapshots">The most snapshot points kept</param>
    /// <param name="maxBoots">The most boots kept</param>
    /// <param name="maxObservedRuns">The most runs of observed hours kept</param>
    /// <param name="maxPrograms">The most programs tracked</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lookback"/> is not positive</exception>
    public ForensicsAnalysis(TimeSpan lookback, int maxSnapshots, int maxBoots, int maxObservedRuns, int maxPrograms)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lookback, TimeSpan.Zero);

        _lookback = lookback;
        _maxBoots = maxBoots;
        _observed = new ObservedHours(maxObservedRuns);
        _series = new SnapshotSeries(maxSnapshots);
        _consumers = new ConsumerTracker(maxPrograms);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the log sources whose lines count as observation of the system: <c>journal</c> and <c>syslog</c>.
    /// </summary>
    public static IReadOnlyList<string> SystemLogSources => _systemLogSources;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a log line; only lines of the system log sources count as observed hours.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="source">The log source</param>
    /// <exception cref="InvalidOperationException">The analysis is sealed</exception>
    public void AddLogLine(DateTimeOffset capturedAt, string source)
    {
        RequireOpen();

        _logLines++;
        _firstLogLineAt = Earlier(_firstLogLineAt, capturedAt);
        _lastLogLineAt = Later(_lastLogLineAt, capturedAt);

        if (_systemLogSources.Contains(source))
        {
            _observed.Add(capturedAt);
        }
    }

    /// <summary>
    /// Adds a boot, in any order: the boots are sorted when the analysis is sealed. Boots beyond the cap are dropped and mark the
    /// boot rule unknown.
    /// </summary>
    /// <param name="at">The time of the boot</param>
    /// <exception cref="InvalidOperationException">The analysis is sealed</exception>
    public void AddBoot(DateTimeOffset at)
    {
        RequireOpen();

        if (_boots.Count < _maxBoots)
        {
            _boots.Add(at.UtcTicks);
        }
        else
        {
            _bootsTruncated = true;
        }
    }

    /// <summary>
    /// Adds a process snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    /// <exception cref="InvalidOperationException">The analysis is sealed</exception>
    /// <exception cref="ArgumentException">The time is earlier than the previous snapshot</exception>
    public void AddProcessSnapshot(DateTimeOffset capturedAt, ProcessSnapshot snapshot)
    {
        RequireOpen();

        var programs = MemoryAnalysis.Programs(snapshot);

        _series.Add(capturedAt, programs);
        _consumers.Add(capturedAt, programs);
        _snapshots++;
        _firstSnapshotAt ??= capturedAt;
        _lastSnapshotAt = capturedAt;
    }

    /// <summary>
    /// Ends the collection of lines, boots and snapshots and computes the threshold and the base rates.
    /// </summary>
    /// <exception cref="InvalidOperationException">The analysis is sealed already</exception>
    public void Seal()
    {
        RequireOpen();

        _sealed = true;
        _boots.Sort();
        _panelThreshold = _series.PanelThresholdBytes();

        foreach (var end in _observed.Ends())
        {
            var from = Before(end);

            Tally(_hoursWith, _hoursKnown, BackupIndex, _series.Backup(from, end));
            Tally(_hoursWith, _hoursKnown, BootIndex, JudgeBoot(from, end).Indicator);
            Tally(_hoursWith, _hoursKnown, PanelIndex, _series.PanelHigh(from, end, _panelThreshold));
        }
    }

    /// <summary>
    /// Judges what ran or happened before an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>The activities</returns>
    /// <exception cref="InvalidOperationException">The analysis is not sealed</exception>
    public IncidentActivities Activities(Incident incident)
    {
        RequireSealed();

        var at = incident.FirstKillAt;
        var from = Before(at);
        var boot = JudgeBoot(from, at);

        return new IncidentActivities
               {
                   Backup = Settle(_series.Backup(from, at), _series.Truncated),
                   Boot = Settle(boot.Indicator, _bootsTruncated),
                   LastBootAt = boot.LastBootAt,
                   PanelHigh = Settle(_series.PanelHigh(from, at, _panelThreshold), _series.Truncated)
               };
    }

    /// <summary>
    /// Counts an incident. Only counters are kept (24 hour counts, the yes and known counts per activity, the first and last
    /// outage, the kill sum, saturating), never a list of incidents.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <exception cref="InvalidOperationException">The analysis is not sealed</exception>
    public void AddIncident(Incident incident)
    {
        var activities = Activities(incident);
        var kills = Math.Max(incident.KillCount, 0);

        _incidents++;
        _kills = long.MaxValue - _kills < kills ? long.MaxValue : _kills + kills;
        _hourOutages[incident.FirstKillAt.UtcDateTime.Hour]++;
        _firstOutageAt = Earlier(_firstOutageAt, incident.FirstKillAt);
        _lastOutageAt = Later(_lastOutageAt, incident.FirstKillAt);
        Tally(_outagesWith, _outagesKnown, BackupIndex, activities.Backup);
        Tally(_outagesWith, _outagesKnown, BootIndex, activities.Boot);
        Tally(_outagesWith, _outagesKnown, PanelIndex, activities.PanelHigh);
    }

    /// <summary>
    /// Evaluates the rules and collects the whole-period result.
    /// </summary>
    /// <param name="generatedAt">The generation time</param>
    /// <returns>The summary</returns>
    /// <exception cref="InvalidOperationException">The analysis is not sealed</exception>
    public ForensicsSummary Summarize(DateTimeOffset generatedAt)
    {
        RequireSealed();

        var hourLevel = ReportLimits.SignificanceLevel / HoursPerDay;
        var timeOfDay = Enumerable.Range(0, HoursPerDay)
                                  .Select(hour => RecurrenceRules.Evaluate(_hourOutages[hour], _incidents, _observed.HoursOfDay[hour], _observed.Count, hourLevel))
                                  .ToList();
        var snapshotLimit = _series.Truncated ? UnknownReason.TooManySnapshots : Runs();

        return new ForensicsSummary
               {
                   GeneratedAt = generatedAt,
                   Lookback = _lookback,
                   LogLines = _logLines,
                   FirstLogLineAt = _firstLogLineAt,
                   LastLogLineAt = _lastLogLineAt,
                   ObservedHours = _observed.Count,
                   Snapshots = _snapshots,
                   FirstSnapshotAt = _firstSnapshotAt,
                   LastSnapshotAt = _lastSnapshotAt,
                   Incidents = _incidents,
                   Kills = _kills,
                   FirstOutageAt = _firstOutageAt,
                   LastOutageAt = _lastOutageAt,
                   TimeOfDay = timeOfDay,
                   Backup = Activity(BackupIndex, snapshotLimit),
                   Boot = Activity(BootIndex, _bootsTruncated ? UnknownReason.TooManyBoots : Runs()),
                   PanelHigh = Activity(PanelIndex, PanelLimit(snapshotLimit)),
                   PanelThresholdBytes = _panelThreshold,
                   Permanent = _consumers.Permanent(ReportLimits.MaxConsumers),
                   Transient = _consumers.Transient(ReportLimits.MaxConsumers),
                   UntrackedProgramEntries = _consumers.UntrackedEntries,
                   SnapshotsTruncated = _series.Truncated,
                   BootsTruncated = _bootsTruncated,
                   ObservedHoursTruncated = _observed.Truncated
               };
    }

    /// <summary>
    /// Returns the earlier of two times, where none is the later.
    /// </summary>
    /// <param name="current">The time so far, if any</param>
    /// <param name="candidate">The new time</param>
    /// <returns>The earlier time</returns>
    private static DateTimeOffset Earlier(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return current is { } time && time <= candidate ? time : candidate;
    }

    /// <summary>
    /// Returns the later of two times, where none is the earlier.
    /// </summary>
    /// <param name="current">The time so far, if any</param>
    /// <param name="candidate">The new time</param>
    /// <returns>The later time</returns>
    private static DateTimeOffset Later(DateTimeOffset? current, DateTimeOffset candidate)
    {
        return current is { } time && time >= candidate ? time : candidate;
    }

    /// <summary>
    /// Counts an indicator: known when it is not unknown, with the property when it is yes.
    /// </summary>
    /// <param name="with">The counts with the property</param>
    /// <param name="known">The counts of known answers</param>
    /// <param name="index">The activity</param>
    /// <param name="indicator">The indicator</param>
    private static void Tally(long[] with, long[] known, int index, Indicator indicator)
    {
        if (indicator == Indicator.Unknown)
        {
            return;
        }

        known[index]++;

        if (indicator == Indicator.Yes)
        {
            with[index]++;
        }
    }

    /// <summary>
    /// Turns a no into unknown when data of the activity was dropped at a cap, because it may have been in the dropped data.
    /// </summary>
    /// <param name="indicator">The indicator</param>
    /// <param name="truncated">Whether data of the activity was dropped</param>
    /// <returns>The indicator</returns>
    private static Indicator Settle(Indicator indicator, bool truncated)
    {
        return truncated && indicator == Indicator.No ? Indicator.Unknown : indicator;
    }

    /// <summary>
    /// Builds the result of a rule that a limit of the report makes unknown.
    /// </summary>
    /// <param name="reason">The limit</param>
    /// <param name="matching">The outages that match</param>
    /// <param name="known">The outages with a known answer</param>
    /// <returns>The unknown result</returns>
    private static PatternResult Limited(UnknownReason reason, long matching, long known)
    {
        return new PatternResult
               {
                   Outcome = PatternOutcome.Unknown,
                   Reason = reason,
                   Matching = matching,
                   Known = known
               };
    }

    /// <summary>
    /// Returns the moment a lookback starts, at least the smallest representable time.
    /// </summary>
    /// <param name="at">The moment</param>
    /// <returns>The start of the lookback</returns>
    private DateTimeOffset Before(DateTimeOffset at)
    {
        var ticks = at.UtcTicks - _lookback.Ticks;

        return ticks <= DateTimeOffset.MinValue.UtcTicks ? DateTimeOffset.MinValue : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    /// <summary>
    /// Returns why the observed hours limit the rules, if they do.
    /// </summary>
    /// <returns>Too many runs, or none</returns>
    private UnknownReason Runs()
    {
        return _observed.Truncated ? UnknownReason.TooManyObservedRuns : UnknownReason.None;
    }

    /// <summary>
    /// Returns why the panel rule is unknown for a limit of the report, if it is.
    /// </summary>
    /// <param name="snapshotLimit">The limit of the snapshot series and the observed hours, or none</param>
    /// <returns>The reason, or none</returns>
    private UnknownReason PanelLimit(UnknownReason snapshotLimit)
    {
        if (snapshotLimit == UnknownReason.None && _panelThreshold == 0)
        {
            return UnknownReason.NoPanelMemory;
        }

        return snapshotLimit;
    }

    /// <summary>
    /// Evaluates the rule of an activity, unless a limit makes it unknown.
    /// </summary>
    /// <param name="index">The activity</param>
    /// <param name="limit">The limit that applies, or none</param>
    /// <returns>The result</returns>
    private PatternResult Activity(int index, UnknownReason limit)
    {
        if (limit != UnknownReason.None)
        {
            return Limited(limit, _outagesWith[index], _outagesKnown[index]);
        }

        return RecurrenceRules.Evaluate(_outagesWith[index], _outagesKnown[index], _hoursWith[index], _hoursKnown[index], ReportLimits.SignificanceLevel);
    }

    /// <summary>
    /// Judges whether a boot lies in a range: unknown when an hour of the range is not observed.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, exclusive</param>
    /// <returns>The indicator and, for yes, the time of the last boot in the range</returns>
    private (Indicator Indicator, DateTimeOffset? LastBootAt) JudgeBoot(DateTimeOffset from, DateTimeOffset to)
    {
        if (from < to && _observed.Covers(from, to))
        {
            return BootIn(from, to);
        }

        return (Indicator.Unknown, null);
    }

    /// <summary>
    /// Judges whether a boot lies in a range of observed hours.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, exclusive</param>
    /// <returns>The indicator and, for yes, the time of the last boot in the range</returns>
    private (Indicator Indicator, DateTimeOffset? LastBootAt) BootIn(DateTimeOffset from, DateTimeOffset to)
    {
        var low = 0;
        var high = _boots.Count;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_boots[middle] < to.UtcTicks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low > 0 && _boots[low - 1] >= from.UtcTicks)
        {
            return (Indicator.Yes, new DateTimeOffset(_boots[low - 1], TimeSpan.Zero));
        }

        return (Indicator.No, null);
    }

    /// <summary>
    /// Requires that the analysis still takes lines, boots and snapshots.
    /// </summary>
    /// <exception cref="InvalidOperationException">The analysis is sealed</exception>
    private void RequireOpen()
    {
        if (_sealed)
        {
            throw new InvalidOperationException("forensics analysis: already sealed");
        }
    }

    /// <summary>
    /// Requires that the analysis is sealed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The analysis is not sealed</exception>
    private void RequireSealed()
    {
        if (_sealed)
        {
            return;
        }

        throw new InvalidOperationException("forensics analysis: not sealed yet");
    }

    #endregion // Methods
}