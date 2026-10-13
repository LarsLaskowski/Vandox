using Vandox.Core.Incidents;

namespace Vandox.Core.Reports;

/// <summary>
/// A capped series of compact process snapshot points that answers the lookback questions. A point is its time, its panel
/// memory and the number of snapshots with a backup program up to it.
/// </summary>
public sealed class SnapshotSeries
{
    #region Constants

    private const long PercentileNumerator = 9;
    private const long PercentileDenominator = 10;

    #endregion // Constants

    #region Fields

    private readonly int _capacity;
    private readonly List<(long Ticks, ulong Panel, int Backups)> _points = [];
    private long _lastTicks;
    private bool _any;
    private int[]? _highPrefix;
    private ulong _highThreshold;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotSeries"/> class with the default capacity.
    /// </summary>
    public SnapshotSeries()
        : this(ReportLimits.MaxSnapshots)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotSeries"/> class.
    /// </summary>
    /// <param name="capacity">The most points kept</param>
    public SnapshotSeries(int capacity)
    {
        _capacity = capacity;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of points kept.
    /// </summary>
    public int Count => _points.Count;

    /// <summary>
    /// Gets a value indicating whether points beyond the capacity were dropped.
    /// </summary>
    public bool Truncated { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs of the snapshot</param>
    /// <exception cref="ArgumentException">The time is earlier than the previous one</exception>
    public void Add(DateTimeOffset capturedAt, IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        var ticks = capturedAt.UtcTicks;

        if (_any && ticks < _lastTicks)
        {
            throw new ArgumentException("snapshot series: a time is earlier than the previous one", nameof(capturedAt));
        }

        _any = true;
        _lastTicks = ticks;

        if (_points.Count >= _capacity)
        {
            Truncated = true;

            return;
        }

        var before = _points.Count > 0 ? _points[^1].Backups : 0;

        _points.Add((ticks, PanelPrograms.RssBytes(programs), before + (HasBackup(programs) ? 1 : 0)));
        _highPrefix = null;
    }

    /// <summary>
    /// Computes the panel memory that counts as high: the nearest-rank percentile of the panel memory of all snapshots.
    /// </summary>
    /// <returns>The threshold in bytes; 0 without panel memory</returns>
    public ulong PanelThresholdBytes()
    {
        if (_points.Count == 0)
        {
            return 0;
        }

        var values = _points.Select(point => point.Panel).Order().ToArray();
        var rank = ((values.LongLength * PercentileNumerator) + PercentileDenominator - 1) / PercentileDenominator;

        return values[rank - 1];
    }

    /// <summary>
    /// Judges whether the backup program ran in a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, exclusive</param>
    /// <returns>Unknown without a snapshot in the range, yes with the backup program there, else no</returns>
    public Indicator Backup(DateTimeOffset from, DateTimeOffset to)
    {
        var low = LowerBound(from.UtcTicks);
        var high = LowerBound(to.UtcTicks);

        if (low >= high)
        {
            return Indicator.Unknown;
        }

        var backups = _points[high - 1].Backups - (low > 0 ? _points[low - 1].Backups : 0);

        return backups > 0 ? Indicator.Yes : Indicator.No;
    }

    /// <summary>
    /// Judges whether the panel memory was high in a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, exclusive</param>
    /// <param name="thresholdBytes">The threshold in bytes</param>
    /// <returns>Unknown without a threshold or a snapshot in the range, yes at or above the threshold, else no</returns>
    public Indicator PanelHigh(DateTimeOffset from, DateTimeOffset to, ulong thresholdBytes)
    {
        var low = LowerBound(from.UtcTicks);
        var high = LowerBound(to.UtcTicks);

        if (thresholdBytes == 0 || low >= high)
        {
            return Indicator.Unknown;
        }

        var prefix = HighPrefix(thresholdBytes);

        return prefix[high] - prefix[low] > 0 ? Indicator.Yes : Indicator.No;
    }

    /// <summary>
    /// Tells whether a snapshot holds a backup program.
    /// </summary>
    /// <param name="programs">The programs of the snapshot</param>
    /// <returns><c>true</c> when a backup program is among them</returns>
    private static bool HasBackup(IReadOnlyDictionary<string, ProgramUsage> programs)
    {
        return BackupPrograms.Names.Any(programs.ContainsKey);
    }

    /// <summary>
    /// Finds the first point at or after a time.
    /// </summary>
    /// <param name="ticks">The ticks of the time</param>
    /// <returns>The index of the point, or the number of points when none is at or after the time</returns>
    private int LowerBound(long ticks)
    {
        var low = 0;
        var high = _points.Count;

        while (low < high)
        {
            var middle = low + ((high - low) / 2);

            if (_points[middle].Ticks < ticks)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// Returns the number of points at or above a threshold up to each point; the array for the last threshold is kept.
    /// </summary>
    /// <param name="thresholdBytes">The threshold in bytes</param>
    /// <returns>The counts, one more than there are points, starting with 0</returns>
    private int[] HighPrefix(ulong thresholdBytes)
    {
        if (_highPrefix is not null && _highThreshold == thresholdBytes)
        {
            return _highPrefix;
        }

        var prefix = new int[_points.Count + 1];

        for (var index = 0; index < _points.Count; index++)
        {
            prefix[index + 1] = prefix[index] + (_points[index].Panel >= thresholdBytes ? 1 : 0);
        }

        _highPrefix = prefix;
        _highThreshold = thresholdBytes;

        return prefix;
    }

    #endregion // Methods
}