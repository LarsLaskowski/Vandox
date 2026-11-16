using System.Globalization;

using Vandox.Backend.Incidents;
using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Overview;

/// <summary>
/// Turns the reads of the overview into its model.
/// </summary>
public static class OverviewView
{
    #region Constants

    /// <summary>
    /// The number of newest metric records scanned for a live one.
    /// </summary>
    public const int LiveScanLimit = 20000;

    /// <summary>
    /// The maximum number of units shown.
    /// </summary>
    public const int MaxUnits = 256;

    /// <summary>
    /// The maximum number of mounts shown.
    /// </summary>
    public const int MaxMounts = 16;

    /// <summary>
    /// The number of incident summaries read.
    /// </summary>
    public const int IncidentSummaries = 20;

    private const string Unknown = "unknown";

    private const string AgentSource = "agent";

    private const string StaleMark = " stale";

    private const string System = "system";

    private const string MariaDb = "mariadb";

    private const string FilesystemAvailable = "filesystem_available_bytes";

    private const string FilesystemSize = "filesystem_size_bytes";

    private const string PressureName = "pressure_avg60";

    private const string NoLiveData = "No live data has arrived yet. Install the agent, or import logs as the README describes.";

    private const string NoServices = "No service state is stored.";

    private const string NoIncidents = "No incidents are stored. Run vandoxd incidents after an import.";

    private const string NoOpenIncident = "No open incident.";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The interval of the refresh.
    /// </summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The deadline of one refresh.
    /// </summary>
    public static readonly TimeSpan RefreshDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The age after which data counts as stale.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The age after which the record of a unit counts as stale.
    /// </summary>
    public static readonly TimeSpan UnitStaleAfter = TimeSpan.FromMinutes(65);

    /// <summary>
    /// The age after which a unit that reports no more is left out.
    /// </summary>
    public static readonly TimeSpan UnitRetirement = TimeSpan.FromHours(2);

    /// <summary>
    /// The window in which the values of the mounts are read.
    /// </summary>
    public static readonly TimeSpan MountWindow = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the series the overview reads.
    /// </summary>
    public static IReadOnlyList<SeriesKey> Series { get; } = [
                                                                 new(System, "mem_available_bytes"),
                                                                 new(System, "mem_total_bytes"),
                                                                 new(System, "swap_used_bytes"),
                                                                 new(System, "swap_total_bytes"),
                                                                 new(System, PressureName),
                                                                 new(System, FilesystemAvailable),
                                                                 new(System, FilesystemSize),
                                                                 new(MariaDb, "mariadb_memory_max_bytes"),
                                                                 new(AgentSource, "agent_spool_bytes"),
                                                                 new(AgentSource, "agent_spool_age_seconds")
                                                             ];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the model of the overview.
    /// </summary>
    /// <param name="data">The reads of one refresh</param>
    /// <param name="now">The current time</param>
    /// <returns>The model</returns>
    public static OverviewModel Build(OverviewData data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);

        var model = new OverviewModel();

        FillFreshness(model, data, now);
        FillServices(model, data, now);
        FillFigures(model, data, now);
        FillIncidents(model, data, now);

        return model;
    }

    /// <summary>
    /// Fills the data freshness texts.
    /// </summary>
    /// <param name="model">The model</param>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    private static void FillFreshness(OverviewModel model, OverviewData data, DateTimeOffset now)
    {
        var freshness = data.Freshness;

        if (freshness.AgentId.Length == 0)
        {
            model.LiveData = NoLiveData;
            model.Received = Unknown;
        }
        else
        {
            model.LiveData = DescribeLive(freshness, now);
            model.Received = freshness.ReceivedAt is { } received ? Describe(received, now) : Unknown + StaleMark;
        }

        model.Backfill = DescribeBackfill(freshness, now);
        model.Spool = DescribeSpool(data, now);
    }

    /// <summary>
    /// Describes the newest live record, or the oldest record scanned when none was live.
    /// </summary>
    /// <param name="freshness">The freshness read</param>
    /// <param name="now">The current time</param>
    /// <returns>The text</returns>
    private static string DescribeLive(AgentFreshness freshness, DateTimeOffset now)
    {
        if (freshness.LastLiveAt is { } live)
        {
            return Describe(live, now);
        }

        return freshness.ScannedBackTo is { } oldest ? $"before {LogsView.ShowSeconds(oldest)}{StaleMark}" : Unknown + StaleMark;
    }

    /// <summary>
    /// Describes the backfill from the open sequence gaps of the agent of the newest batch.
    /// </summary>
    /// <param name="freshness">The freshness read</param>
    /// <param name="now">The current time</param>
    /// <returns>The text</returns>
    private static string DescribeBackfill(AgentFreshness freshness, DateTimeOffset now)
    {
        if (freshness.AgentId.Length == 0 || freshness.OpenSequenceGaps == 0)
        {
            return "No backfill running.";
        }

        var span = $"data from {Time(freshness.MissingStart)} to {Time(freshness.MissingEnd)} is missing";

        if (freshness.ReceivedAt is { } received && IsStale(received, now) == false)
        {
            return $"Backfill running: {span}.";
        }

        return $"Backfill stalled: {span}, no data received since {Time(freshness.ReceivedAt)}.";
    }

    /// <summary>
    /// Describes the spool of the agent from its latest self sample.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <returns>The text</returns>
    private static string DescribeSpool(OverviewData data, DateTimeOffset now)
    {
        var bytes = Latest(data, AgentSource, "agent_spool_bytes");
        var age = Latest(data, AgentSource, "agent_spool_age_seconds");

        if (bytes is null || age is null)
        {
            return Unknown;
        }

        var sample = bytes.CapturedAt;
        var oldest = age.Value > 0 ? $"oldest unsent record {LogsView.ShowSeconds(sample - TimeSpan.FromSeconds(age.Value))}" : "nothing unsent";

        return $"{OverviewText.Size(bytes.Value)}, {oldest}, as of {LogsView.ShowSeconds(sample)}{(IsStale(sample, now) ? StaleMark : string.Empty)}";
    }

    /// <summary>
    /// Fills the service rows and their texts.
    /// </summary>
    /// <param name="model">The model</param>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    private static void FillServices(OverviewModel model, OverviewData data, DateTimeOffset now)
    {
        var page = data.Services;

        if (page.States.Count == 0)
        {
            model.ServicesText = NoServices;

            return;
        }

        var rows = page.States.Take(MaxUnits).Select(state => UnitRow(state, now)).ToList();

        model.Units = rows;
        model.MoreUnits = page.Total > rows.Count ? string.Create(CultureInfo.InvariantCulture, $"and {page.Total - rows.Count} more") : string.Empty;

        if (data.Freshness.ReceivedAt is not { } received || IsStale(received, now))
        {
            model.ServicesStale = $"stale: no data received since {Time(data.Freshness.ReceivedAt)}";
        }
    }

    /// <summary>
    /// Creates the row of a unit.
    /// </summary>
    /// <param name="state">The state</param>
    /// <param name="now">The current time</param>
    /// <returns>The row</returns>
    private static OverviewUnitRow UnitRow(LatestServiceState state, DateTimeOffset now)
    {
        var active = DateTimeOffset.TryParse(state.ActiveEnterAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) ? LogsView.ShowSeconds(since) : Unknown;

        return new OverviewUnitRow
               {
                   Unit = LogText.Runs(state.Unit, LogText.MaxShortChars, IncidentsView.MaxTextRuns),
                   State = $"{state.ActiveState} ({state.SubState})",
                   ActiveSince = active,
                   Restarts = state.Restarts.ToString(CultureInfo.InvariantCulture),
                   Recorded = LogsView.ShowSeconds(state.CapturedAt),
                   Failed = state.ActiveState == "failed",
                   Stale = now - state.CapturedAt > UnitStaleAfter
               };
    }

    /// <summary>
    /// Fills the key figures in their fixed order.
    /// </summary>
    /// <param name="model">The model</param>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    private static void FillFigures(OverviewModel model, OverviewData data, DateTimeOffset now)
    {
        var figures = new List<OverviewFigureRow>
                      {
                          AvailableMemory(data, now),
                          Swap(data, now),
                          PressureRow(data, now, "Memory pressure", "memory", true),
                          PressureRow(data, now, "CPU pressure", "cpu", false),
                          PressureRow(data, now, "I/O pressure", "io", true)
                      };

        model.MoreMounts = AddDisks(figures, data, now);
        figures.AddRange(MariaDbRows(data, now));
        model.Figures = figures;
    }

    /// <summary>
    /// Creates the row of the available memory.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <returns>The row</returns>
    private static OverviewFigureRow AvailableMemory(OverviewData data, DateTimeOffset now)
    {
        var available = Latest(data, System, "mem_available_bytes");
        var total = Latest(data, System, "mem_total_bytes");

        if (available is null)
        {
            return Row("Available memory", null, null, now);
        }

        if (total is not { Value: > 0 })
        {
            return Row("Available memory", OverviewText.Size(available.Value), available.CapturedAt, now);
        }

        return Row("Available memory", $"{OverviewText.Size(available.Value)} ({OverviewText.Share(available.Value, total.Value)} of {OverviewText.Size(total.Value)})", Older(available, total), now);
    }

    /// <summary>
    /// Creates the row of the swap.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <returns>The row</returns>
    private static OverviewFigureRow Swap(OverviewData data, DateTimeOffset now)
    {
        var used = Latest(data, System, "swap_used_bytes");
        var total = Latest(data, System, "swap_total_bytes");

        if (total is null || used is null)
        {
            return Row("Swap", null, null, now);
        }

        if (total.Value <= 0)
        {
            return Row("Swap", "no swap", Older(used, total), now);
        }

        return Row("Swap", $"{OverviewText.Size(used.Value)} of {OverviewText.Size(total.Value)} used ({OverviewText.Share(used.Value, total.Value)})", Older(used, total), now);
    }

    /// <summary>
    /// Creates the row of the pressure of a resource.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <param name="figure">The name of the figure</param>
    /// <param name="resource">The resource</param>
    /// <param name="withFull">Whether the resource has a full pressure</param>
    /// <returns>The row</returns>
    private static OverviewFigureRow PressureRow(OverviewData data, DateTimeOffset now, string figure, string resource, bool withFull)
    {
        var some = PressureOf(data, resource, "some");
        var full = withFull ? PressureOf(data, resource, "full") : null;

        if (some is null || (withFull && full is null))
        {
            return Row(figure, null, null, now);
        }

        var text = $"some {Percent(some.Value)}";

        if (full is null)
        {
            return Row(figure, text, some.CapturedAt, now);
        }

        return Row(figure, $"{text}, full {Percent(full.Value)}", Older(some, full), now);
    }

    /// <summary>
    /// Adds the row of each mount, or one unknown row without any.
    /// </summary>
    /// <param name="figures">The rows</param>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <returns>The text of the mounts not shown</returns>
    private static string AddDisks(List<OverviewFigureRow> figures, OverviewData data, DateTimeOffset now)
    {
        var available = Find(data, System, FilesystemAvailable);
        var sizes = Find(data, System, FilesystemSize)?.Values ?? [];

        if (available is null || available.Values.Count == 0)
        {
            figures.Add(Row("Disk", null, null, now));

            return string.Empty;
        }

        var shown = available.Values.Take(MaxMounts).ToList();

        foreach (var value in shown)
        {
            var mount = MountOf(value);
            var size = sizes.FirstOrDefault(candidate => MountOf(candidate) == mount);
            var text = $"{OverviewText.Size(value.Value)} available";
            var at = value.CapturedAt;

            if (size is { Value: > 0 })
            {
                text += $" ({OverviewText.Share(value.Value, size.Value)}) of {OverviewText.Size(size.Value)}";
                at = Older(value, size);
            }

            var row = Row("Disk", text, at, now);

            row.Mount = LogText.Runs(mount, LogText.MaxShortChars, IncidentsView.MaxTextRuns);
            figures.Add(row);
        }

        return available.LabelSets > shown.Count ? string.Create(CultureInfo.InvariantCulture, $"and {available.LabelSets - shown.Count} more mounts") : string.Empty;
    }

    /// <summary>
    /// Creates the three MariaDB rows.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    /// <returns>The rows</returns>
    private static IEnumerable<OverviewFigureRow> MariaDbRows(OverviewData data, DateTimeOffset now)
    {
        var status = data.MariaDb;

        if (status is null)
        {
            return [Row("MariaDB", null, null, now), Row("MariaDB connections", null, null, now), Row("MariaDB memory", null, null, now)];
        }

        var up = status.Availability == MariaDbStatus.Up;
        var availability = status.Availability switch
                           {
                               MariaDbStatus.Up => "up",
                               MariaDbStatus.Down => "down",
                               MariaDbStatus.NotAnswering => "not answering",
                               _ => null
                           };
        var connections = up && status.ThreadsConnected is { } threads && status.MaxConnections is { } maximum ? string.Create(CultureInfo.InvariantCulture, $"{threads:0} of {maximum}") : null;
        var memoryMax = Latest(data, MariaDb, "mariadb_memory_max_bytes");
        var memoryLimit = memoryMax is null ? string.Empty : $" of at most {OverviewText.Size(memoryMax.Value)}";
        var memory = up && status.MemoryUsed is { } used ? OverviewText.Size(used) + memoryLimit : null;
        var memoryAt = memoryMax is not null && memoryMax.CapturedAt < status.CapturedAt ? memoryMax.CapturedAt : status.CapturedAt;

        return [
                   Row("MariaDB", availability, status.CapturedAt, now),
                   Row("MariaDB connections", connections, status.CapturedAt, now),
                   Row("MariaDB memory", memory, memoryAt, now)
               ];
    }

    /// <summary>
    /// Fills the incident rows and their text.
    /// </summary>
    /// <param name="model">The model</param>
    /// <param name="data">The reads</param>
    /// <param name="now">The current time</param>
    private static void FillIncidents(OverviewModel model, OverviewData data, DateTimeOffset now)
    {
        if (data.Incidents.Count == 0)
        {
            model.IncidentsText = NoIncidents;

            return;
        }

        model.OpenIncidents = data.Incidents.Where(incident => incident.End > now && incident.FirstKillAt <= now).Select(incident => IncidentRow(incident, now)).ToList();
        model.LatestIncident = IncidentRow(data.Incidents[0], now);
        model.IncidentsText = model.OpenIncidents.Count == 0 ? NoOpenIncident : string.Empty;
    }

    /// <summary>
    /// Creates the row of an incident.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <param name="now">The current time</param>
    /// <returns>The row</returns>
    private static OverviewIncidentRow IncidentRow(IncidentSummary incident, DateTimeOffset now)
    {
        return new OverviewIncidentRow
               {
                   Number = incident.Id,
                   Href = IncidentsView.IncidentHref(incident.Id),
                   FirstKill = LogsView.ShowSeconds(incident.FirstKillAt),
                   Victim = LogText.Runs(incident.FirstVictim, LogText.MaxShortChars, IncidentsView.MaxTextRuns),
                   Future = incident.FirstKillAt > now
               };
    }

    /// <summary>
    /// Creates a figure row; without a value or a time it shows unknown.
    /// </summary>
    /// <param name="figure">The name of the figure</param>
    /// <param name="value">The value text, or <c>null</c> for unknown</param>
    /// <param name="at">The time of the value</param>
    /// <param name="now">The current time</param>
    /// <returns>The row</returns>
    private static OverviewFigureRow Row(string figure, string? value, DateTimeOffset? at, DateTimeOffset now)
    {
        if (value is null || at is null)
        {
            return new OverviewFigureRow
                   {
                       Figure = figure,
                       Value = Unknown
                   };
        }

        return new OverviewFigureRow
               {
                   Figure = figure,
                   Value = value,
                   Time = LogsView.ShowSeconds(at.Value),
                   Stale = IsStale(at.Value, now)
               };
    }

    /// <summary>
    /// Finds the result of a series.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <returns>The result, or <c>null</c> when the read has none</returns>
    private static LatestSeries? Find(OverviewData data, string source, string name)
    {
        return data.Values.FirstOrDefault(series => series.Series.Source == source && series.Series.Name == name);
    }

    /// <summary>
    /// Gets the first value of a series.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="source">The source</param>
    /// <param name="name">The metric name</param>
    /// <returns>The value, or <c>null</c></returns>
    private static LatestValue? Latest(OverviewData data, string source, string name)
    {
        var values = Find(data, source, name)?.Values;

        return values is { Count: > 0 } ? values[0] : null;
    }

    /// <summary>
    /// Gets the pressure value of a resource and kind.
    /// </summary>
    /// <param name="data">The reads</param>
    /// <param name="resource">The resource</param>
    /// <param name="kind">The kind</param>
    /// <returns>The value, or <c>null</c></returns>
    private static LatestValue? PressureOf(OverviewData data, string resource, string kind)
    {
        return Find(data, System, PressureName)?.Values.FirstOrDefault(value => LabelOf(value, "resource") == resource && LabelOf(value, "kind") == kind);
    }

    /// <summary>
    /// Gets the mount label of a value.
    /// </summary>
    /// <param name="value">The value</param>
    /// <returns>The mount, or an empty text</returns>
    private static string MountOf(LatestValue value)
    {
        return LabelOf(value, "mount");
    }

    /// <summary>
    /// Gets a label of a value.
    /// </summary>
    /// <param name="value">The value</param>
    /// <param name="key">The label name</param>
    /// <returns>The label, or an empty text</returns>
    private static string LabelOf(LatestValue value, string key)
    {
        return value.Labels.TryGetValue(key, out var label) ? label : string.Empty;
    }

    /// <summary>
    /// Gets the older capture time of two values.
    /// </summary>
    /// <param name="first">The first value</param>
    /// <param name="second">The second value</param>
    /// <returns>The older time</returns>
    private static DateTimeOffset Older(LatestValue first, LatestValue second)
    {
        return first.CapturedAt < second.CapturedAt ? first.CapturedAt : second.CapturedAt;
    }

    /// <summary>
    /// Formats a percentage value with one decimal.
    /// </summary>
    /// <param name="value">The value in percent</param>
    /// <returns>The text</returns>
    private static string Percent(double value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} %");
    }

    /// <summary>
    /// Formats a time, or unknown for none.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    private static string Time(DateTimeOffset? time)
    {
        return time is { } value ? LogsView.ShowSeconds(value) : Unknown;
    }

    /// <summary>
    /// Tells whether a time is more than the stale limit before now.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="now">The current time</param>
    /// <returns><c>true</c> when the time is stale</returns>
    private static bool IsStale(DateTimeOffset time, DateTimeOffset now)
    {
        return now - time > StaleAfter;
    }

    /// <summary>
    /// Describes a time with its age, marked stale when it is.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="now">The current time</param>
    /// <returns>The text</returns>
    private static string Describe(DateTimeOffset time, DateTimeOffset now)
    {
        var age = time > now ? $"ahead by {OverviewText.Age(time - now)}" : $"{OverviewText.Age(now - time)} ago";

        return $"{LogsView.ShowSeconds(time)} ({age}){(IsStale(time, now) ? StaleMark : string.Empty)}";
    }

    #endregion // Methods
}