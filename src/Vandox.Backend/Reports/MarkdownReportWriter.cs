using System.Globalization;
using System.Text;

using Vandox.Core.Incidents;
using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Writes the forensics report as Markdown. Victim commands and program names are external text and are written only through
/// <see cref="MarkdownText.Code"/>; the other text comes from the <see cref="ReportText"/> table, the numbers and the times.
/// </summary>
public sealed class MarkdownReportWriter : IReportWriter
{
    #region Constants

    private const string Dash = "—";
    private const string RangeDash = "–";
    private const ulong MibBytes = 1048576;

    #endregion // Constants

    #region Fields

    private readonly TextWriter _writer;
    private readonly ReportText _text;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownReportWriter"/> class.
    /// </summary>
    /// <param name="writer">The output</param>
    /// <param name="text">The labels of the language</param>
    public MarkdownReportWriter(TextWriter writer, ReportText text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(text);

        _writer = writer;
        _text = text;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Formats a time as the UTC instant, to the minute.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The text, such as <c>2026-10-02 06:59 UTC</c></returns>
    private static string Time(DateTimeOffset at)
    {
        return at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    }

    /// <summary>
    /// Formats a time that may be missing.
    /// </summary>
    /// <param name="at">The time, if any</param>
    /// <returns>The text, or a dash</returns>
    private static string Time(DateTimeOffset? at)
    {
        return at is { } time ? Time(time) : Dash;
    }

    /// <summary>
    /// Formats a whole number with ASCII digits.
    /// </summary>
    /// <param name="value">The number</param>
    /// <returns>The text</returns>
    private static string Number(long value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats bytes as MiB, rounded half away from zero.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns>The whole MiB</returns>
    private static string Mib(ulong bytes)
    {
        var whole = bytes / MibBytes;
        var rounded = bytes % MibBytes >= MibBytes / 2 ? whole + 1 : whole;

        return rounded.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Fills the placeholders of a text with invariant formatting.
    /// </summary>
    /// <param name="template">The text</param>
    /// <param name="arguments">The values</param>
    /// <returns>The text</returns>
    private static string Format(string template, params object[] arguments)
    {
        return string.Format(CultureInfo.InvariantCulture, template, arguments);
    }

    /// <summary>
    /// Formats the hour of the day as a range of one hour.
    /// </summary>
    /// <param name="hour">The hour, 0 to 23</param>
    /// <returns>The range, such as <c>02:00–03:00</c></returns>
    private static string HourRange(int hour)
    {
        return Format("{0:00}:00{1}{2:00}:00", hour, RangeDash, (hour + 1) % 24);
    }

    /// <summary>
    /// Appends a list item.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="label">The label</param>
    /// <param name="value">The value</param>
    /// <param name="indent">The indentation, for a nested item</param>
    private static void Item(StringBuilder builder, string label, string value, string indent = "")
    {
        builder.Append(indent).Append("- ").Append(label).Append(": ").Append(value).Append('\n');
    }

    /// <summary>
    /// Appends a table row.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="cells">The cells</param>
    private static void Row(StringBuilder builder, params string[] cells)
    {
        builder.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
    }

    /// <summary>
    /// Appends the header of a table with a blank line before it.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="alignRight">Which columns are numbers</param>
    /// <param name="titles">The column titles</param>
    private static void TableHead(StringBuilder builder, bool[] alignRight, params string[] titles)
    {
        Row(builder, titles);
        Row(builder, alignRight.Select(right => right ? "---:" : "---").ToArray());
    }

    /// <summary>
    /// Lists the distinct victims of the kept kills in first-seen order, each with its count.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>The text</returns>
    private string Victims(Incident incident)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var command in incident.Kills.Select(kill => kill.VictimCommand))
        {
            if (counts.TryGetValue(command, out var count))
            {
                counts[command] = count + 1;
            }
            else
            {
                counts[command] = 1;
                order.Add(command);
            }
        }

        if (order.Count == 0)
        {
            return _text.Unknown;
        }

        return string.Join(", ", order.Select(victim => $"{(victim.Length == 0 ? _text.Unknown : MarkdownText.Code(victim))} ({Number(counts[victim])})"));
    }

    /// <summary>
    /// Lists the growth candidates of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <returns>The text; unknown when the growth is not known, none when it is known and empty</returns>
    private string Growth(IncidentHypothesis hypothesis)
    {
        if (hypothesis.GrowthKnown)
        {
            return hypothesis.Growth.Count == 0 ? _text.None : string.Join(", ", hypothesis.Growth.Select(growth => $"{MarkdownText.Code(growth.Program)} +{Mib(growth.GrowthBytes)} MiB"));
        }

        return _text.Unknown;
    }

    /// <summary>
    /// Lists the largest consumers of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <returns>The text; unknown when the incident had no snapshot</returns>
    private string Consumers(IncidentHypothesis hypothesis)
    {
        if (hypothesis.SnapshotCount == 0)
        {
            return _text.Unknown;
        }

        return hypothesis.Consumers.Count == 0 ? _text.None : string.Join(", ", hypothesis.Consumers.Select(usage => $"{MarkdownText.Code(usage.Program)} {Mib(usage.RssBytes)} MiB"));
    }

    /// <summary>
    /// Returns the word of an indicator.
    /// </summary>
    /// <param name="indicator">The indicator</param>
    /// <returns>Yes, no or unknown</returns>
    private string Word(Indicator indicator)
    {
        return indicator switch
               {
                   Indicator.Yes => _text.Yes,
                   Indicator.No => _text.No,
                   _ => _text.Unknown
               };
    }

    /// <summary>
    /// Returns the word of the outcome of a rule.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns>Detected, not detected or unknown</returns>
    private string Word(PatternOutcome outcome)
    {
        return outcome switch
               {
                   PatternOutcome.Detected => _text.Detected,
                   PatternOutcome.NotDetected => _text.NotDetected,
                   _ => _text.Unknown
               };
    }

    /// <summary>
    /// Returns the text of a reason a rule is unknown.
    /// </summary>
    /// <param name="reason">The reason</param>
    /// <returns>The text; a dash for no reason</returns>
    private string ReasonText(UnknownReason reason)
    {
        return reason switch
               {
                   UnknownReason.TooFewIncidents => _text.ReasonTooFewIncidents,
                   UnknownReason.NoBaseData => _text.ReasonNoBaseData,
                   UnknownReason.TooManySnapshots => _text.ReasonTooManySnapshots,
                   UnknownReason.TooManyBoots => _text.ReasonTooManyBoots,
                   UnknownReason.TooManyObservedRuns => _text.ReasonTooManyObservedRuns,
                   UnknownReason.NoPanelMemory => _text.ReasonNoPanelMemory,
                   _ => Dash
               };
    }

    /// <summary>
    /// Formats a share as a percent with one decimal, a half away from zero, and the decimal separator of the language.
    /// </summary>
    /// <param name="share">The share from 0 to 1, if known</param>
    /// <returns>The text, or a dash</returns>
    private string Percent(double? share)
    {
        if (share is not { } value)
        {
            return Dash;
        }

        var text = Math.Round(value * 100, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture);

        return text.Replace('.', _text.DecimalSeparator) + " %";
    }

    /// <summary>
    /// Writes the activity of a boot with the time of the last one.
    /// </summary>
    /// <param name="activities">The activities</param>
    /// <returns>The text</returns>
    private string BootText(IncidentActivities activities)
    {
        var word = Word(activities.Boot);

        return activities.LastBootAt is { } last ? $"{word} ({Format(_text.LastBoot, Time(last))})" : word;
    }

    /// <summary>
    /// Appends the head: the title and the facts about the data.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    private void AppendHead(StringBuilder builder, ForensicsSummary summary)
    {
        builder.Append("# ").Append(_text.Title).Append("\n\n");
        Item(builder, _text.GeneratedAt, Time(summary.GeneratedAt));
        Item(builder, _text.LogLines, Counted(summary.LogLines, summary.FirstLogLineAt, summary.LastLogLineAt));
        Item(builder, _text.ObservedHours, Number(summary.ObservedHours));
        Item(builder, _text.Snapshots, Counted(summary.Snapshots, summary.FirstSnapshotAt, summary.LastSnapshotAt));
        Item(builder, _text.Lookback, Number((long)summary.Lookback.TotalMinutes));
        builder.Append("- ").Append(_text.AllTimesUtc).Append("\n\n");
    }

    /// <summary>
    /// Writes a count with its first and last time, when there is any.
    /// </summary>
    /// <param name="count">The count</param>
    /// <param name="first">The first time</param>
    /// <param name="last">The last time</param>
    /// <returns>The text</returns>
    private string Counted(long count, DateTimeOffset? first, DateTimeOffset? last)
    {
        return first is null || last is null ? Number(count) : Format(_text.CountWithRange, Number(count), Time(first), Time(last));
    }

    /// <summary>
    /// Appends the summary: the counts, one line per detected pattern and the rules without a result.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    private void AppendSummary(StringBuilder builder, ForensicsSummary summary)
    {
        var patterns = DetectedPatterns(summary);
        var unknown = UnknownRules(summary);

        builder.Append("## ").Append(_text.SummaryHeading).Append("\n\n");
        builder.Append("- ").Append(Format(_text.Outages, Number(summary.Incidents), Number(summary.Kills))).Append('\n');

        if (summary.FirstOutageAt is not null)
        {
            builder.Append("- ").Append(Format(_text.FirstOutage, Time(summary.FirstOutageAt))).Append('\n');
            builder.Append("- ").Append(Format(_text.LastOutage, Time(summary.LastOutageAt))).Append('\n');
        }

        patterns.ForEach(pattern => builder.Append("- ").Append(pattern).Append('\n'));

        if (unknown.Count > 0)
        {
            builder.Append("- ").Append(Format(_text.UnknownRules, string.Join(", ", unknown))).Append('\n');
        }

        if (patterns.Count == 0)
        {
            builder.Append("- ").Append(unknown.Count == 0 ? _text.NoPattern : _text.NoPatternAmongDecided).Append('\n');
        }

        builder.Append('\n');
    }

    /// <summary>
    /// Writes one line for each detected pattern: the hours of the day and the activities.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <returns>The lines</returns>
    private List<string> DetectedPatterns(ForensicsSummary summary)
    {
        var lines = new List<string>();

        for (var hour = 0; hour < summary.TimeOfDay.Count; hour++)
        {
            if (summary.TimeOfDay[hour].Outcome == PatternOutcome.Detected)
            {
                lines.Add(PatternLine($"{_text.TimeOfDay} {HourRange(hour)} UTC", summary.TimeOfDay[hour]));
            }
        }

        AddDetected(lines, _text.Backup, summary.Backup);
        AddDetected(lines, _text.Boot, summary.Boot);
        AddDetected(lines, _text.PanelHigh, summary.PanelHigh);

        return lines;
    }

    /// <summary>
    /// Adds the line of a pattern when the rule detected it.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="name">The name of the rule</param>
    /// <param name="result">The result</param>
    private void AddDetected(List<string> lines, string name, PatternResult result)
    {
        if (result.Outcome == PatternOutcome.Detected)
        {
            lines.Add(PatternLine(name, result));
        }
    }

    /// <summary>
    /// Writes the line of a detected pattern.
    /// </summary>
    /// <param name="name">The name of the rule</param>
    /// <param name="result">The result</param>
    /// <returns>The line</returns>
    private string PatternLine(string name, PatternResult result)
    {
        return Format(_text.PatternLine, name, Number(result.Matching), Number(result.Known), Percent(result.Share), Percent(result.BaseShare));
    }

    /// <summary>
    /// Names the rules whose result is unknown, each with its reason.
    /// </summary>
    /// <param name="summary">The summary</param>
    /// <returns>The names</returns>
    private List<string> UnknownRules(ForensicsSummary summary)
    {
        var names = new List<string>();
        var hours = summary.TimeOfDay.FirstOrDefault(result => result.Outcome == PatternOutcome.Unknown);

        if (summary.TimeOfDay.Count == 0 || hours is not null)
        {
            names.Add(NamedReason(_text.TimeOfDay, hours?.Reason ?? UnknownReason.None));
        }

        foreach (var (name, result) in new[] { (_text.Backup, summary.Backup), (_text.Boot, summary.Boot), (_text.PanelHigh, summary.PanelHigh) })
        {
            if (result.Outcome == PatternOutcome.Unknown)
            {
                names.Add(NamedReason(name, result.Reason));
            }
        }

        return names;
    }

    /// <summary>
    /// Writes the name of a rule with its reason.
    /// </summary>
    /// <param name="name">The name</param>
    /// <param name="reason">The reason</param>
    /// <returns>The text</returns>
    private string NamedReason(string name, UnknownReason reason)
    {
        return reason == UnknownReason.None ? name : $"{name} ({ReasonText(reason)})";
    }

    /// <summary>
    /// Appends the recurring patterns: the hours of the day, the activities and the panel threshold.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    private void AppendPatterns(StringBuilder builder, ForensicsSummary summary)
    {
        builder.Append("## ").Append(_text.PatternsHeading).Append("\n\n");
        builder.Append("### ").Append(_text.TimeOfDayHeading).Append("\n\n");
        AppendHours(builder, summary);
        builder.Append("### ").Append(_text.ActivitiesHeading).Append("\n\n");
        TableHead(builder, [false, true, true, true, true, false, false], _text.Activity, _text.OutagesWith, _text.Known, _text.OutageShare, _text.BaseShare, _text.Result, _text.Reason);
        ActivityRow(builder, _text.Backup, summary.Backup);
        ActivityRow(builder, _text.Boot, summary.Boot);
        ActivityRow(builder, _text.PanelHigh, summary.PanelHigh);
        builder.Append('\n');
        builder.Append(Format(_text.PanelThreshold, summary.PanelThresholdBytes == 0 ? _text.None : Mib(summary.PanelThresholdBytes) + " MiB")).Append("\n\n");
    }

    /// <summary>
    /// Appends the table of the hours of the day that have an outage, or the reason the rule is unknown.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    private void AppendHours(StringBuilder builder, ForensicsSummary summary)
    {
        var unknown = summary.TimeOfDay.FirstOrDefault(result => result.Outcome == PatternOutcome.Unknown);

        if (summary.TimeOfDay.Count == 0 || unknown is not null)
        {
            builder.Append(_text.Result).Append(": ").Append(_text.Unknown).Append(" (").Append(ReasonText(unknown?.Reason ?? UnknownReason.None)).Append(")\n\n");

            return;
        }

        var hours = Enumerable.Range(0, summary.TimeOfDay.Count).Where(hour => summary.TimeOfDay[hour].Matching > 0).ToList();

        if (hours.Count == 0)
        {
            builder.Append(_text.None).Append("\n\n");

            return;
        }

        TableHead(builder, [false, true, true, true, false], _text.Hour, _text.OutageCount, _text.OutageShare, _text.BaseShare, _text.Result);

        foreach (var hour in hours)
        {
            var result = summary.TimeOfDay[hour];

            Row(builder, HourRange(hour), Number(result.Matching), Percent(result.Share), Percent(result.BaseShare), Word(result.Outcome));
        }

        builder.Append('\n');
    }

    /// <summary>
    /// Appends the row of an activity.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="name">The name of the activity</param>
    /// <param name="result">The result of its rule</param>
    private void ActivityRow(StringBuilder builder, string name, PatternResult result)
    {
        Row(builder, name, Number(result.Matching), Number(result.Known), Percent(result.Share), Percent(result.BaseShare), Word(result.Outcome), ReasonText(result.Reason));
    }

    /// <summary>
    /// Appends the permanent and transient consumers.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    private void AppendConsumers(StringBuilder builder, ForensicsSummary summary)
    {
        builder.Append("## ").Append(_text.MemoryHeading).Append("\n\n");
        builder.Append("### ").Append(_text.PermanentHeading).Append("\n\n");
        AppendConsumerList(builder, summary, summary.Permanent.Select(consumer => Format(_text.ConsumerPermanent, MarkdownText.Code(consumer.Program), Percent(consumer.Share), Mib(consumer.MeanRssBytes), Mib(consumer.PeakRssBytes))));
        builder.Append("### ").Append(_text.TransientHeading).Append("\n\n");
        AppendConsumerList(builder, summary, summary.Transient.Select(consumer => Format(_text.ConsumerTransient, MarkdownText.Code(consumer.Program), Percent(consumer.Share), Mib(consumer.PeakRssBytes), Time(consumer.PeakAt))));
    }

    /// <summary>
    /// Appends a list of consumers, or the text that says why there is none.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="summary">The summary</param>
    /// <param name="lines">The lines of the consumers</param>
    private void AppendConsumerList(StringBuilder builder, ForensicsSummary summary, IEnumerable<string> lines)
    {
        var items = lines.ToList();

        if (summary.Snapshots == 0)
        {
            builder.Append(_text.NoSnapshots).Append("\n\n");
        }
        else if (items.Count == 0)
        {
            builder.Append(_text.None).Append("\n\n");
        }
        else
        {
            items.ForEach(item => builder.Append("- ").Append(item).Append('\n'));
            builder.Append('\n');
        }
    }

    #endregion // Methods

    #region IReportWriter

    /// <inheritdoc />
    public async Task WriteSummaryAsync(ForensicsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();

        AppendHead(builder, summary);
        AppendSummary(builder, summary);
        AppendPatterns(builder, summary);
        AppendConsumers(builder, summary);
        builder.Append("## ").Append(_text.OutagesHeading).Append("\n\n");

        if (summary.Incidents == 0)
        {
            builder.Append(_text.NoOutages).Append("\n\n");
        }

        await _writer.WriteAsync(builder.ToString()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteIncidentAsync(StoredIncident stored, IncidentActivities activities)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(activities);

        var incident = stored.Incident;
        var hypothesis = incident.Hypothesis;
        var builder = new StringBuilder();

        builder.Append("### ").Append(string.Format(CultureInfo.InvariantCulture, _text.OutageHeading, stored.Id, Time(incident.FirstKillAt))).Append("\n\n");
        Item(builder, _text.Window, $"{Time(incident.Start)} {RangeDash} {Time(incident.End)}");
        Item(builder, _text.Kills, Number(incident.KillCount));
        Item(builder, _text.Victims, Victims(incident));

        var notKept = incident.KillCount - incident.Kills.Count;

        if (notKept > 0)
        {
            Item(builder, _text.KillsNotKept, Number(notKept));
        }

        Item(builder, _text.Growth, Growth(hypothesis));
        Item(builder, _text.Consumers, Consumers(hypothesis));
        builder.Append("- ").Append(_text.Activities).Append(":\n");
        Item(builder, _text.Backup, Word(activities.Backup), "  ");
        Item(builder, _text.Boot, BootText(activities), "  ");
        Item(builder, _text.PanelHigh, Word(activities.PanelHigh), "  ");
        Item(builder, _text.ServiceEvents, Number(hypothesis.ServiceEvents));
        builder.Append('\n');

        await _writer.WriteAsync(builder.ToString()).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task WriteNotesAsync(ForensicsSummary summary, long listed)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var builder = new StringBuilder();

        builder.Append("## ").Append(_text.NotesHeading).Append("\n\n");
        builder.Append("- ").Append(_text.RulesNote).Append('\n');

        if (summary.SnapshotsTruncated)
        {
            builder.Append("- ").Append(Format(_text.CapSnapshotsNote, Number(ReportLimits.MaxSnapshots))).Append('\n');
        }

        if (summary.BootsTruncated)
        {
            builder.Append("- ").Append(Format(_text.CapBootsNote, Number(ReportLimits.MaxBoots))).Append('\n');
        }

        if (summary.ObservedHoursTruncated)
        {
            builder.Append("- ").Append(Format(_text.CapRunsNote, Number(ReportLimits.MaxObservedRuns))).Append('\n');
        }

        if (summary.UntrackedProgramEntries > 0)
        {
            builder.Append("- ").Append(Format(_text.UntrackedNote, Number(summary.UntrackedProgramEntries), Number(ReportLimits.MaxPrograms))).Append('\n');
        }

        if (listed != summary.Incidents)
        {
            builder.Append("- ").Append(_text.IncidentsChangedNote).Append('\n');
        }

        await _writer.WriteAsync(builder.ToString()).ConfigureAwait(false);
    }

    #endregion // IReportWriter
}