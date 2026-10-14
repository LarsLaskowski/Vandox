using System.Globalization;

using Vandox.Backend.Logs;
using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// One incident, as its page shows it.
/// </summary>
public sealed class IncidentDetailView
{
    #region Properties

    /// <summary>
    /// Gets the number.
    /// </summary>
    public long Id { get; init; }

    /// <summary>
    /// Gets the start of the window.
    /// </summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>
    /// Gets the end of the window.
    /// </summary>
    public DateTimeOffset End { get; init; }

    /// <summary>
    /// Gets the time of the first kill.
    /// </summary>
    public DateTimeOffset FirstKillAt { get; init; }

    /// <summary>
    /// Gets the duration from the first to the last kill as text.
    /// </summary>
    public string Duration { get; init; } = string.Empty;

    /// <summary>
    /// Gets the time the incident was first stored.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets the time the incident was last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Gets the time of the last kill.
    /// </summary>
    public DateTimeOffset LastKillAt { get; init; }

    /// <summary>
    /// Gets the number of kills.
    /// </summary>
    public long KillCount { get; init; }

    /// <summary>
    /// Gets the kills.
    /// </summary>
    public IReadOnlyList<KillRowView> Kills { get; init; } = [];

    /// <summary>
    /// Gets the programs that grew most.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> Growth { get; init; } = [];

    /// <summary>
    /// Gets the text of the growth state.
    /// </summary>
    public string GrowthState { get; init; } = string.Empty;

    /// <summary>
    /// Gets the largest consumers.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> Consumers { get; init; } = [];

    /// <summary>
    /// Gets the text of the backup state.
    /// </summary>
    public string Backup { get; init; } = string.Empty;

    /// <summary>
    /// Gets the names of the backup programs that ran.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TextRun>> BackupPrograms { get; init; } = [];

    /// <summary>
    /// Gets the time of the last boot before the incident, or <c>null</c> without one.
    /// </summary>
    public DateTimeOffset? RecentBootAt { get; init; }

    /// <summary>
    /// Gets the number of process snapshots the hypothesis rests on.
    /// </summary>
    public long SnapshotCount { get; init; }

    /// <summary>
    /// Gets the number of service events in the window.
    /// </summary>
    public long ServiceEvents { get; init; }

    /// <summary>
    /// Gets the timeline in stored order.
    /// </summary>
    public IReadOnlyList<TimelineRowView> Timeline { get; init; } = [];

    /// <summary>
    /// Gets the number of timeline entries the stored incident omits.
    /// </summary>
    public long TimelineOmitted { get; init; }

    /// <summary>
    /// Gets the time of the connection peak, or <c>null</c> without one.
    /// </summary>
    public DateTimeOffset? PeakAt { get; init; }

    /// <summary>
    /// Gets the number of sockets at the peak.
    /// </summary>
    public long PeakSockets { get; init; }

    /// <summary>
    /// Gets the number of connection snapshots the peak was found in.
    /// </summary>
    public long PeakSnapshots { get; init; }

    /// <summary>
    /// Gets the commands with the most sockets at the peak.
    /// </summary>
    public IReadOnlyList<ProgramAmountView> PeakProcesses { get; init; } = [];

    /// <summary>
    /// Gets the metric extremes.
    /// </summary>
    public IReadOnlyList<ExtremeRowView> Extremes { get; init; } = [];

    /// <summary>
    /// Gets the new templates.
    /// </summary>
    public IReadOnlyList<IncidentTemplateView> NewTemplates { get; init; } = [];

    /// <summary>
    /// Gets the template spikes.
    /// </summary>
    public IReadOnlyList<IncidentTemplateView> TemplateSpikes { get; init; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Builds the view of a stored incident.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <returns>The view</returns>
    public static IncidentDetailView From(StoredIncident stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var incident = stored.Incident;
        var hypothesis = incident.Hypothesis;
        var growth = hypothesis.GrowthKnown ? hypothesis.Growth.Select(item => Amount(item.Program, IncidentsView.Growth(item.GrowthBytes))).ToList() : [];
        var peak = incident.Connections;

        return new IncidentDetailView
               {
                   Id = stored.Id,
                   Start = incident.Start,
                   End = incident.End,
                   FirstKillAt = incident.FirstKillAt,
                   LastKillAt = incident.LastKillAt,
                   Duration = IncidentsView.Duration(incident.LastKillAt - incident.FirstKillAt),
                   CreatedAt = stored.CreatedAt,
                   UpdatedAt = stored.UpdatedAt,
                   KillCount = incident.KillCount,
                   Kills = incident.Kills.Select(Kill).ToList(),
                   Growth = growth,
                   GrowthState = IncidentRowView.GrowthText(hypothesis, growth.Count),
                   Consumers = hypothesis.Consumers.Select(item => Amount(item.Program, IncidentsView.Mib(item.RssBytes))).ToList(),
                   Backup = IncidentRowView.BackupText(hypothesis),
                   BackupPrograms = hypothesis.BackupPrograms.Select(name => LogText.Runs(name, LogText.MaxShortChars)).ToList(),
                   RecentBootAt = hypothesis.RecentBootAt,
                   SnapshotCount = hypothesis.SnapshotCount,
                   ServiceEvents = hypothesis.ServiceEvents,
                   Timeline = incident.Timeline.Select(Entry).ToList(),
                   TimelineOmitted = incident.TimelineOmitted,
                   PeakAt = peak?.At,
                   PeakSockets = peak?.Sockets ?? 0,
                   PeakSnapshots = peak?.Snapshots ?? 0,
                   PeakProcesses = peak?.Processes.Select(process => Amount(process.Command, Number(process.Count))).ToList() ?? [],
                   Extremes = incident.Metrics.Select(Extreme).ToList(),
                   NewTemplates = incident.NewTemplates.Select(template => Template(template.Source, template.Program, template.Template, template.FirstSeen, template.Count, string.Empty)).ToList(),
                   TemplateSpikes = incident.TemplateSpikes.Select(spike => Template(spike.Source, spike.Program, spike.Template, spike.Hour, spike.Count, Number(spike.UsualLevel))).ToList()
               };
    }

    /// <summary>
    /// Gets the CSS class of the marker of a timeline entry type from the fixed map; any other type has none.
    /// </summary>
    /// <param name="type">The type of the entry</param>
    /// <returns>The class, or an empty text</returns>
    private static string Marker(string type)
    {
        return type switch
               {
                   "oom_kill" => "mark-oom",
                   "boot" => "mark-boot",
                   "backup_first_seen" or "backup_last_seen" => "mark-backup",
                   _ => string.Empty
               };
    }

    /// <summary>
    /// Formats a whole number.
    /// </summary>
    /// <param name="value">The number</param>
    /// <returns>The text</returns>
    private static string Number(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a number with up to six decimals.
    /// </summary>
    /// <param name="value">The number</param>
    /// <returns>The text</returns>
    private static string Number(double value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a program with an amount.
    /// </summary>
    /// <param name="program">The name of the program</param>
    /// <param name="amount">The amount as text</param>
    /// <returns>The view</returns>
    private static ProgramAmountView Amount(string program, string amount)
    {
        return new ProgramAmountView
               {
                   Program = LogText.Runs(program, LogText.MaxShortChars),
                   Amount = amount
               };
    }

    /// <summary>
    /// Builds the row of a kill.
    /// </summary>
    /// <param name="kill">The kill</param>
    /// <returns>The row</returns>
    private static KillRowView Kill(IncidentKill kill)
    {
        return new KillRowView
               {
                   At = kill.At,
                   LogsHref = IncidentsView.LogsHref(kill.At),
                   Host = LogText.Runs(kill.Host, LogText.MaxShortChars),
                   Victim = LogText.Runs(kill.VictimCommand, LogText.MaxShortChars),
                   Pid = kill.VictimPid is null ? string.Empty : Number(kill.VictimPid.Value),
                   AnonRss = kill.AnonRssBytes is null or < 0 ? string.Empty : IncidentsView.Mib((ulong)kill.AnonRssBytes.Value),
                   OomScoreAdj = kill.OomScoreAdj is null ? string.Empty : Number(kill.OomScoreAdj.Value)
               };
    }

    /// <summary>
    /// Builds the row of a timeline entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <returns>The row</returns>
    private static TimelineRowView Entry(TimelineEntry entry)
    {
        var texts = entry.Texts.Select(pair => $"{pair.Key}: {pair.Value}").Concat(entry.Numbers.Select(pair => $"{pair.Key}: {Number(pair.Value)}"));

        return new TimelineRowView
               {
                   At = entry.At,
                   LogsHref = IncidentsView.LogsHref(entry.At),
                   Marker = Marker(entry.Type),
                   Type = LogText.Runs(entry.Type, LogText.MaxShortChars),
                   Host = LogText.Runs(entry.Host, LogText.MaxShortChars),
                   Texts = texts.Select(text => LogText.Runs(text, IncidentsView.MaxTimelineTextChars)).ToList(),
                   LineCount = entry.LineCount
               };
    }

    /// <summary>
    /// Builds the row of a metric extreme.
    /// </summary>
    /// <param name="metric">The extreme</param>
    /// <returns>The row</returns>
    private static ExtremeRowView Extreme(MetricExtreme metric)
    {
        var labels = metric.Labels is { Count: > 0 } ? $" {{{string.Join(", ", metric.Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"))}}}" : string.Empty;
        var bytes = metric.Name.EndsWith("_bytes", StringComparison.Ordinal) && metric.Value is >= 0 and < 1.8e19;

        return new ExtremeRowView
               {
                   Name = LogText.Runs($"{metric.Name}{labels}", LogText.MaxShortChars),
                   Extreme = LogText.Runs(metric.Extreme, LogText.MaxShortChars),
                   Source = LogText.Runs(metric.Source, LogText.MaxShortChars),
                   At = metric.At,
                   LogsHref = IncidentsView.LogsHref(metric.At),
                   Value = bytes ? IncidentsView.Mib((ulong)metric.Value) : Number(metric.Value)
               };
    }

    /// <summary>
    /// Builds the row of a new template or a template spike.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="template">The template</param>
    /// <param name="at">The time</param>
    /// <param name="count">The number of lines</param>
    /// <param name="usualLevel">The usual level as text</param>
    /// <returns>The row</returns>
    private static IncidentTemplateView Template(string source, string program, string template, DateTimeOffset at, long count, string usualLevel)
    {
        return new IncidentTemplateView
               {
                   Source = LogText.Runs(source, LogText.MaxShortChars),
                   Program = LogText.Runs(program, LogText.MaxShortChars),
                   Template = LogText.Runs(template, LogText.MaxShortChars),
                   At = at,
                   LogsHref = IncidentsView.LogsHref(at),
                   Count = count,
                   UsualLevel = usualLevel
               };
    }

    #endregion // Methods
}