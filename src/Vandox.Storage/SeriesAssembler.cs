namespace Vandox.Storage;

/// <summary>
/// Puts the lines read for a query on one time axis: the steps of a rollup result, or the union of the capture times of a raw result with the breaks of its lines.
/// </summary>
internal static class SeriesAssembler
{
    #region Methods

    /// <summary>
    /// Builds the lines of a step result: a value at each step that holds a bucket, else a gap.
    /// </summary>
    /// <param name="data">The lines read</param>
    /// <returns>The lines</returns>
    internal static List<SeriesLine> Steps(SeriesData data)
    {
        return data.Lines.Select(entry => StepLine(entry.Key, entry.Line, data.Axis)).ToList();
    }

    /// <summary>
    /// Builds the axis and the lines of a raw result. A line breaks between two of its values when a gap that concerns it, or a whole UTC-aligned
    /// five-minute span without a value, lies between them; a time is added there when the axis has none, at most one per interval.
    /// </summary>
    /// <param name="lines">The lines read</param>
    /// <param name="gaps">The gaps of the result in order of their start</param>
    /// <returns>The times and the lines</returns>
    internal static (List<DateTimeOffset> Times, List<SeriesLine> Lines) Raw(IReadOnlyList<(SeriesKey Key, MetricLine Line)> lines, IReadOnlyList<SeriesGap> gaps)
    {
        var axis = new SortedSet<long>(lines.SelectMany(entry => entry.Line.Points).Select(point => point.At.UtcTicks));
        var indexes = new Dictionary<SeriesKey, GapIndex>();
        var breaks = new List<List<(long After, long Before)>>();

        foreach (var (key, line) in lines)
        {
            if (indexes.TryGetValue(key, out var known) == false)
            {
                known = new GapIndex(gaps.Where(gap => gap.Concerns(key.Source, key.Name)).ToList());
                indexes[key] = known;
            }

            breaks.Add(FindBreaks(line, known, axis));
        }

        var times = axis.ToArray();
        var result = lines.Select((entry, index) => RawLine(entry.Key, entry.Line, indexes[entry.Key], breaks[index], times)).ToList();

        return (times.Select(time => new DateTimeOffset(time, TimeSpan.Zero)).ToList(), result);
    }

    /// <summary>
    /// Finds the intervals between two consecutive values of a line that break it, and adds the time of each break to the axis where it has none.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="index">The gaps that concern the line</param>
    /// <param name="axis">The axis; times may be added</param>
    /// <returns>The broken intervals</returns>
    private static List<(long After, long Before)> FindBreaks(MetricLine line, GapIndex index, SortedSet<long> axis)
    {
        var broken = new List<(long After, long Before)>();
        var times = line.Points.Select(point => point.At.UtcTicks).ToList();

        for (var position = 1; position < times.Count; position++)
        {
            var start = BreakStart(index, times[position - 1], times[position]);

            if (start is null)
            {
                continue;
            }

            broken.Add((times[position - 1], times[position]));
            AddTime(axis, times[position - 1], times[position], start.Value);
        }

        return broken;
    }

    /// <summary>
    /// Finds where the break between two values starts: the earlier of the start of a gap that concerns the line and overlaps the interval, and the start of the first whole aligned five-minute span inside it.
    /// </summary>
    /// <param name="index">The gaps that concern the line</param>
    /// <param name="after">The earlier value's time</param>
    /// <param name="before">The later value's time</param>
    /// <returns>The start in ticks, or <c>null</c> when the line stays connected</returns>
    private static long? BreakStart(GapIndex index, long after, long before)
    {
        var gap = index.FirstOverlapStart(after, before);
        var span = ((SeriesTime.FloorDiv(after - SeriesTime.EpochTicks, SeriesTime.FiveMinuteTicks) + 1) * SeriesTime.FiveMinuteTicks) + SeriesTime.EpochTicks;

        if (span + SeriesTime.FiveMinuteTicks > before)
        {
            return gap;
        }

        return gap is null ? span : Math.Min(gap.Value, span);
    }

    /// <summary>
    /// Adds the time of a break to the axis unless it already has a time strictly between the two values.
    /// </summary>
    /// <param name="axis">The axis</param>
    /// <param name="after">The earlier value's time</param>
    /// <param name="before">The later value's time</param>
    /// <param name="start">The start of the break</param>
    private static void AddTime(SortedSet<long> axis, long after, long before, long start)
    {
        var low = after + 1;
        var high = before - 1;

        if (low > high || axis.GetViewBetween(low, high).Any())
        {
            return;
        }

        axis.Add(Math.Min(Math.Max(low, start), high));
    }

    /// <summary>
    /// Builds one raw line on the final axis: its own value, a gap inside a gap that concerns it or inside one of its broken intervals, else between samples.
    /// </summary>
    /// <param name="key">The series</param>
    /// <param name="line">The line</param>
    /// <param name="index">The gaps that concern the line</param>
    /// <param name="broken">The broken intervals of the line in order</param>
    /// <param name="times">The axis</param>
    /// <returns>The line</returns>
    private static SeriesLine RawLine(SeriesKey key, MetricLine line, GapIndex index, List<(long After, long Before)> broken, long[] times)
    {
        var own = line.Points.ToDictionary(point => point.At.UtcTicks);
        var points = new List<SeriesPoint>(times.Length);
        var position = 0;

        foreach (var time in times)
        {
            while (position < broken.Count && broken[position].Before <= time)
            {
                position++;
            }

            if (own.TryGetValue(time, out var value))
            {
                points.Add(ValuePoint(value));
            }
            else
            {
                var inBreak = position < broken.Count && broken[position].After < time;

                points.Add(new SeriesPoint
                           {
                               State = inBreak || index.Contains(time) ? PointState.Gap : PointState.BetweenSamples
                           });
            }
        }

        return new SeriesLine
               {
                   Source = key.Source,
                   Name = key.Name,
                   Labels = line.Labels,
                   Points = points
               };
    }

    /// <summary>
    /// Builds one step line on the axis.
    /// </summary>
    /// <param name="key">The series</param>
    /// <param name="line">The line</param>
    /// <param name="axis">The starts of the steps as ticks</param>
    /// <returns>The line</returns>
    private static SeriesLine StepLine(SeriesKey key, MetricLine line, IReadOnlyList<long> axis)
    {
        var own = line.Points.ToDictionary(point => point.At.UtcTicks);

        return new SeriesLine
               {
                   Source = key.Source,
                   Name = key.Name,
                   Labels = line.Labels,
                   Points = axis.Select(time => own.TryGetValue(time, out var value)
                                                    ? ValuePoint(value)
                                                    : new SeriesPoint
                                                      {
                                                          State = PointState.Gap
                                                      }).ToList()
               };
    }

    /// <summary>
    /// Builds the point of a read value.
    /// </summary>
    /// <param name="value">The value read</param>
    /// <returns>The point</returns>
    private static SeriesPoint ValuePoint(LinePoint value)
    {
        return new SeriesPoint
               {
                   State = PointState.Value,
                   Minimum = value.Minimum,
                   Average = value.Average,
                   Maximum = value.Maximum,
                   Last = value.Last,
                   LastAt = value.LastAt,
                   Count = value.Count
               };
    }

    #endregion // Methods
}