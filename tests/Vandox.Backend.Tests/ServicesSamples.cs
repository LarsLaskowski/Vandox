using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the inputs of the services view tests: service availability, restart markers, series lines, status results and thread snapshots.
/// </summary>
internal static class ServicesSamples
{
    #region Constants

    /// <summary>
    /// The size of one GiB in bytes.
    /// </summary>
    internal const double Gib = 1073741824;

    /// <summary>
    /// The unit name of the first sample service.
    /// </summary>
    internal const string Nginx = "nginx.service";

    /// <summary>
    /// The unit name of the second sample service.
    /// </summary>
    internal const string PhpFpm = "php-fpm.service";

    /// <summary>
    /// The largest value of a thread's time and identifier: the largest <see cref="ulong"/>.
    /// </summary>
    internal const ulong LargestSeconds = ulong.MaxValue;

    /// <summary>
    /// The right-to-left override character, which external texts must show as an escape.
    /// </summary>
    internal static readonly string Rlo = char.ConvertFromUtf32(0x202E);

    /// <summary>
    /// The zero width space, which external texts must show as an escape.
    /// </summary>
    internal static readonly string Zwsp = char.ConvertFromUtf32(0x200B);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the time of the view tests.
    /// </summary>
    internal static DateTimeOffset Now => HistorySamples.Base;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the availability of one unit.
    /// </summary>
    /// <param name="unit">The unit name</param>
    /// <param name="available">The time available</param>
    /// <param name="notAvailable">The time not available</param>
    /// <param name="unknown">The unknown time</param>
    /// <param name="active">The latest active state</param>
    /// <param name="sub">The latest sub state</param>
    /// <param name="stateAt">The time of the latest record, or <see langword="null"/> for none</param>
    /// <returns>The availability</returns>
    internal static ServiceAvailability Unit(string unit, TimeSpan available, TimeSpan notAvailable, TimeSpan unknown, string active = "active", string sub = "running", DateTimeOffset? stateAt = null)
    {
        return new ServiceAvailability
               {
                   Unit = unit,
                   Available = available,
                   NotAvailable = notAvailable,
                   Unknown = unknown,
                   ActiveState = active,
                   SubState = sub,
                   StateAt = stateAt
               };
    }

    /// <summary>
    /// Creates the result of an availability read that names every unit as evaluated.
    /// </summary>
    /// <param name="units">The units</param>
    /// <returns>The result</returns>
    internal static ServiceAvailabilityResult Availability(params ServiceAvailability[] units)
    {
        return new ServiceAvailabilityResult
               {
                   Units = units,
                   UnitCount = units.Length
               };
    }

    /// <summary>
    /// Creates the marker of a service restart.
    /// </summary>
    /// <param name="at">The time of the restart</param>
    /// <param name="unit">The unit</param>
    /// <returns>The marker</returns>
    internal static SeriesMarker Restart(DateTimeOffset at, string unit)
    {
        return new SeriesMarker
               {
                   Kind = MarkerKind.ServiceRestart,
                   At = at,
                   Text = unit
               };
    }

    /// <summary>
    /// Creates a record of a service state that follows another record of the same unit with one restart less.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="unit">The unit</param>
    /// <param name="restarts">The restart count of the record</param>
    /// <returns>The record, with the record before it</returns>
    internal static ServiceStateRow RestartRecord(DateTimeOffset at, string unit, long restarts)
    {
        return new ServiceStateRow
               {
                   Unit = unit,
                   ActiveState = "active",
                   CapturedAt = at,
                   Restarts = restarts,
                   Previous = new ServiceStateRow
                              {
                                  Unit = unit,
                                  ActiveState = "active",
                                  CapturedAt = at.AddMinutes(-30),
                                  Restarts = restarts - 1
                              }
               };
    }

    /// <summary>
    /// Gives the cells of a row as their texts joined with a bar.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The text</returns>
    internal static string Cells(SnapshotRowView row)
    {
        return string.Join('|', row.Cells.Select(HistorySamples.Text));
    }

    /// <summary>
    /// Gives the texts of the cells of a row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <returns>The texts</returns>
    internal static string[] CellTexts(SnapshotRowView row)
    {
        return [.. row.Cells.Select(HistorySamples.Text)];
    }

    /// <summary>
    /// Creates a point with a value.
    /// </summary>
    /// <param name="value">The average, minimum, maximum and last value</param>
    /// <returns>The point</returns>
    internal static SeriesPoint Value(double value)
    {
        return Value(value, value);
    }

    /// <summary>
    /// Creates a point with a value whose last value differs from its average.
    /// </summary>
    /// <param name="average">The average, minimum and maximum</param>
    /// <param name="last">The last value</param>
    /// <returns>The point</returns>
    internal static SeriesPoint Value(double average, double last)
    {
        return new SeriesPoint
               {
                   State = PointState.Value,
                   Minimum = average,
                   Average = average,
                   Maximum = average,
                   Last = last,
                   LastAt = Now,
                   Count = 1
               };
    }

    /// <summary>
    /// Creates a point without a value.
    /// </summary>
    /// <returns>The point</returns>
    internal static SeriesPoint Gap()
    {
        return new SeriesPoint
               {
                   State = PointState.Gap
               };
    }

    /// <summary>
    /// Creates a point between two samples.
    /// </summary>
    /// <returns>The point</returns>
    internal static SeriesPoint Between()
    {
        return new SeriesPoint
               {
                   State = PointState.BetweenSamples
               };
    }

    /// <summary>
    /// Creates a line of the time-series query.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <param name="points">The points</param>
    /// <returns>The line</returns>
    internal static SeriesLine Line(string source, string name, params SeriesPoint[] points)
    {
        return new SeriesLine
               {
                   Source = source,
                   Name = name,
                   Points = points
               };
    }

    /// <summary>
    /// Gives the times of the steps that overlap a range: the multiples of the step since the epoch.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="step">The step, which divides a day</param>
    /// <returns>The times</returns>
    internal static IReadOnlyList<DateTimeOffset> AxisTimes(DateTimeOffset from, DateTimeOffset to, TimeSpan step)
    {
        var first = Math.DivRem(from.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks, step.Ticks, out var rest);

        first = rest < 0 ? first - 1 : first;

        var last = (to.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks - 1) / step.Ticks;

        return [.. Enumerable.Range(0, (int)(last - first + 1)).Select(index => new DateTimeOffset(DateTimeOffset.UnixEpoch.UtcTicks + ((first + index) * step.Ticks), TimeSpan.Zero))];
    }

    /// <summary>
    /// Creates points with the same value.
    /// </summary>
    /// <param name="count">The number of points</param>
    /// <param name="value">The value</param>
    /// <returns>The points</returns>
    internal static SeriesPoint[] Points(int count, double value)
    {
        return [.. Enumerable.Range(0, count).Select(_ => Value(value))];
    }

    /// <summary>
    /// Creates a result of the time-series query that was read in steps.
    /// </summary>
    /// <param name="times">The times</param>
    /// <param name="step">The step</param>
    /// <param name="lines">The lines</param>
    /// <returns>The result</returns>
    internal static TimeSeriesResult Series(IReadOnlyList<DateTimeOffset> times, TimeSpan step, params SeriesLine[] lines)
    {
        return new TimeSeriesResult
               {
                   Resolution = TimeSeriesResolution.Step,
                   Step = step,
                   Times = times,
                   Lines = lines
               };
    }

    /// <summary>
    /// Creates a status result for the steps of a query: MariaDB up in every step with 1 GiB in use and 5 connections, and 100 records.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="oldest">The time of the oldest record, or <see langword="null"/> for the start of the query</param>
    /// <param name="records">The number of records</param>
    /// <param name="gapsCompleteUntil">The time after which gaps are not read, or <see langword="null"/></param>
    /// <returns>The result</returns>
    internal static MariaDbStatusResult StatusAnswer(MariaDbStatusQuery query, DateTimeOffset? oldest = null, int records = 100, DateTimeOffset? gapsCompleteUntil = null)
    {
        var times = AxisTimes(query.From, query.To, query.Step);

        return new MariaDbStatusResult
               {
                   Times = times,
                   Up = Points(times.Count, 100),
                   Memory = Points(times.Count, Gib),
                   Connections = Points(times.Count, 5),
                   UpTime = query.To - query.From,
                   LatestMemory = 1.2 * Gib,
                   LatestMemoryAt = times[^1],
                   Oldest = oldest ?? query.From,
                   GapsCompleteUntil = gapsCompleteUntil,
                   Records = records
               };
    }

    /// <summary>
    /// Creates the stamp of a thread snapshot.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="at">The capture time</param>
    /// <param name="threads">The recorded threads</param>
    /// <param name="running">The running threads</param>
    /// <param name="longest">The longest time of a running thread in seconds, or <see langword="null"/> for none</param>
    /// <returns>The stamp</returns>
    internal static ThreadSnapshotStamp Stamp(long id, DateTimeOffset at, int threads, int running, long? longest)
    {
        return new ThreadSnapshotStamp
               {
                   RecordId = id,
                   CapturedAt = at,
                   Threads = threads,
                   Running = running,
                   LongestSeconds = longest
               };
    }

    /// <summary>
    /// Creates a running thread.
    /// </summary>
    /// <param name="id">The thread ID</param>
    /// <param name="seconds">The time in seconds</param>
    /// <param name="info">The statement</param>
    /// <returns>The thread</returns>
    internal static MariaDbThread Thread(ulong id, ulong seconds, string info)
    {
        return new MariaDbThread
               {
                   Id = id,
                   User = "app",
                   Host = "10.0.0.5:51234",
                   Db = "shop",
                   Command = "Query",
                   State = "Sending data",
                   TimeSeconds = seconds,
                   Info = info
               };
    }

    /// <summary>
    /// Creates a thread snapshot.
    /// </summary>
    /// <param name="at">The capture time</param>
    /// <param name="threads">The recorded threads</param>
    /// <param name="running">The running threads</param>
    /// <param name="sleeping">The sleeping threads</param>
    /// <param name="rows">The rows of the running threads</param>
    /// <returns>The snapshot</returns>
    internal static ThreadSnapshot Snapshot(DateTimeOffset at, int threads, int running, int sleeping, params MariaDbThread[] rows)
    {
        return new ThreadSnapshot
               {
                   CapturedAt = at,
                   Threads = threads,
                   Running = running,
                   Sleeping = sleeping,
                   Rows = rows
               };
    }

    /// <summary>
    /// Gives the runs of a text as one line per run, so that two displays can be compared.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns>The description</returns>
    internal static string Describe(IReadOnlyList<Logs.TextRun> runs)
    {
        return string.Join('|', runs.Select(run => $"{run.Escaped}:{run.Text}"));
    }

    #endregion // Methods
}