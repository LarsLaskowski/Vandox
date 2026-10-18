using System.Globalization;

using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// Turns the three parts of a report run into blocks, in the words of one language. Victim commands and program names are external text and become
/// external pieces; every other text comes from the <see cref="ReportText"/> table, the numbers and the times.
/// </summary>
internal sealed class ReportComposer
{
    #region Constants

    private const string Dash = "—";
    private const string RangeDash = "–";
    private const ulong MibBytes = 1048576;

    #endregion // Constants

    #region Fields

    private readonly ReportText _text;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ReportComposer"/> class.
    /// </summary>
    /// <param name="text">The labels of the language</param>
    internal ReportComposer(ReportText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        _text = text;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Composes the head, the summary, the patterns, the consumers and the heading of the outages.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Summary(ForensicsSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var blocks = new List<ReportBlock>();

        AddHead(blocks, summary);
        AddSummary(blocks, summary);
        AddPatterns(blocks, summary);
        AddConsumers(blocks, summary);
        AddListeners(blocks, summary.Listeners);
        blocks.Add(Heading(2, _text.OutagesHeading));

        if (summary.Incidents == 0)
        {
            blocks.Add(Paragraph(_text.NoOutages));
        }

        return blocks;
    }

    /// <summary>
    /// Composes the section of one outage.
    /// </summary>
    /// <param name="stored">The stored incident</param>
    /// <param name="activities">The activities before the outage</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Incident(StoredIncident stored, IncidentActivities activities)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(activities);

        var incident = stored.Incident;
        var hypothesis = incident.Hypothesis;
        var notKept = incident.KillCount - incident.Kills.Count;

        var blocks = new List<ReportBlock>
                     {
                         Heading(3, string.Format(CultureInfo.InvariantCulture, _text.OutageHeading, stored.Id, Time(incident.FirstKillAt))),
                         Labeled(_text.Window, $"{Time(incident.Start)} {RangeDash} {Time(incident.End)}"),
                         Labeled(_text.Kills, Number(incident.KillCount)),
                         Labeled(_text.Victims, Victims(incident))
                     };

        if (notKept > 0)
        {
            blocks.Add(Labeled(_text.KillsNotKept, Number(notKept)));
        }

        blocks.Add(Labeled(_text.Growth, Growth(hypothesis)));
        blocks.Add(Labeled(_text.Consumers, Consumers(hypothesis)));
        blocks.Add(Item(0, ReportPiece.Plain(_text.Activities + ":")));
        blocks.Add(Labeled(_text.Backup, Word(activities.Backup), 1));
        blocks.Add(Labeled(_text.Boot, BootText(activities), 1));
        blocks.Add(Labeled(_text.PanelHigh, Word(activities.PanelHigh), 1));
        blocks.Add(Labeled(_text.ServiceEvents, Number(hypothesis.ServiceEvents)));

        return blocks;
    }

    /// <summary>
    /// Composes the notes.
    /// </summary>
    /// <param name="summary">The whole-period result</param>
    /// <param name="listed">The number of incidents of the second pass</param>
    /// <returns>The blocks</returns>
    internal IReadOnlyList<ReportBlock> Notes(ForensicsSummary summary, long listed)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var blocks = new List<ReportBlock>
                     {
                         Heading(2, _text.NotesHeading),
                         Item(_text.RulesNote)
                     };

        if (summary.SnapshotsTruncated)
        {
            blocks.Add(Item(Format(_text.CapSnapshotsNote, Number(ReportLimits.MaxSnapshots))));
        }

        if (summary.BootsTruncated)
        {
            blocks.Add(Item(Format(_text.CapBootsNote, Number(ReportLimits.MaxBoots))));
        }

        if (summary.ObservedHoursTruncated)
        {
            blocks.Add(Item(Format(_text.CapRunsNote, Number(ReportLimits.MaxObservedRuns))));
        }

        if (summary.UntrackedProgramEntries > 0)
        {
            blocks.Add(Item(Format(_text.UntrackedNote, Number(summary.UntrackedProgramEntries), Number(ReportLimits.MaxPrograms))));
        }

        AddListenerNotes(blocks, summary.Listeners);

        if (listed != summary.Incidents)
        {
            blocks.Add(Item(_text.IncidentsChangedNote));
        }

        return blocks;
    }

    /// <summary>
    /// Creates a heading.
    /// </summary>
    /// <param name="level">The level, 1 to 3</param>
    /// <param name="text">The text</param>
    /// <returns>The block</returns>
    private static ReportBlock Heading(int level, string text)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Heading,
                   Level = level,
                   Pieces = [ReportPiece.Plain(text)]
               };
    }

    /// <summary>
    /// Creates a paragraph of plain text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The block</returns>
    private static ReportBlock Paragraph(string text)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Paragraph,
                   Pieces = [ReportPiece.Plain(text)]
               };
    }

    /// <summary>
    /// Creates a list item.
    /// </summary>
    /// <param name="level">The level, 0 or 1</param>
    /// <param name="pieces">The pieces of the text</param>
    /// <returns>The block</returns>
    private static ReportBlock Item(int level, params ReportPiece[] pieces)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Item,
                   Level = level,
                   Pieces = pieces
               };
    }

    /// <summary>
    /// Creates a list item of plain text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The block</returns>
    private static ReportBlock Item(string text)
    {
        return Item(0, ReportPiece.Plain(text));
    }

    /// <summary>
    /// Creates a list item of a label and a plain value.
    /// </summary>
    /// <param name="label">The label</param>
    /// <param name="value">The value</param>
    /// <param name="level">The level, 0 or 1</param>
    /// <returns>The block</returns>
    private static ReportBlock Labeled(string label, string value, int level = 0)
    {
        return Item(level, ReportPiece.Plain($"{label}: {value}"));
    }

    /// <summary>
    /// Creates a list item of a label and a value that holds external text.
    /// </summary>
    /// <param name="label">The label</param>
    /// <param name="value">The pieces of the value</param>
    /// <returns>The block</returns>
    private static ReportBlock Labeled(string label, IEnumerable<ReportPiece> value)
    {
        return Item(0, [ReportPiece.Plain($"{label}: "), .. value]);
    }

    /// <summary>
    /// Creates a table.
    /// </summary>
    /// <param name="alignRight">Which columns are numbers</param>
    /// <param name="rows">The rows, the header first</param>
    /// <returns>The block</returns>
    private static ReportBlock Table(bool[] alignRight, params IReadOnlyList<string>[] rows)
    {
        return new ReportBlock
               {
                   Kind = ReportBlockKind.Table,
                   AlignRight = alignRight,
                   Rows = rows
               };
    }

    /// <summary>
    /// Joins lists of pieces with a plain separator.
    /// </summary>
    /// <param name="parts">The lists</param>
    /// <returns>The pieces</returns>
    private static List<ReportPiece> Joined(IEnumerable<IEnumerable<ReportPiece>> parts)
    {
        var pieces = new List<ReportPiece>();

        foreach (var part in parts)
        {
            if (pieces.Count > 0)
            {
                pieces.Add(ReportPiece.Plain(", "));
            }

            pieces.AddRange(part);
        }

        return pieces;
    }

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
    /// Splits a template at its placeholder <c>{0}</c>, which stands for external text, and fills the other placeholders, which start at <c>{1}</c>.
    /// </summary>
    /// <param name="template">The template</param>
    /// <param name="code">The external text</param>
    /// <param name="arguments">The values of the placeholders from <c>{1}</c></param>
    /// <returns>The pieces: the text before, the external text, the text after</returns>
    private static List<ReportPiece> WithCode(string template, string code, params string[] arguments)
    {
        object[] values = ["{0}", .. arguments];
        var parts = template.Split("{0}", 2, StringSplitOptions.None);
        var pieces = new List<ReportPiece>();

        if (parts[0].Length > 0)
        {
            pieces.Add(ReportPiece.Plain(Format(parts[0], values)));
        }

        if (parts.Length == 1)
        {
            return pieces;
        }

        pieces.Add(ReportPiece.Code(code));

        if (parts[1].Length > 0)
        {
            pieces.Add(ReportPiece.Plain(Format(parts[1], values)));
        }

        return pieces;
    }

    /// <summary>
    /// Tells whether an outcome compared the inventories.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns><c>true</c> for all ports or the ports below 1024 only</returns>
    private static bool IsCompared(ListenerOutcome outcome)
    {
        return outcome is ListenerOutcome.AllPorts or ListenerOutcome.PrivilegedOnly;
    }

    /// <summary>
    /// Sorts findings by agent ID, compared ordinally, then by sequence number.
    /// </summary>
    /// <param name="findings">The findings</param>
    /// <returns>The sorted findings</returns>
    private static List<ListenerFinding> Ordered(IEnumerable<ListenerFinding> findings)
    {
        return findings.OrderBy(finding => finding.AgentId, StringComparer.Ordinal).ThenBy(finding => finding.Seq).ToList();
    }

    /// <summary>
    /// Lists the distinct victims of the kept kills in first-seen order, each with its count.
    /// </summary>
    /// <param name="incident">The incident</param>
    /// <returns>The pieces; unknown when no kill is kept</returns>
    private List<ReportPiece> Victims(Incident incident)
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
            return [ReportPiece.Plain(_text.Unknown)];
        }

        return Joined(order.Select(victim => (IEnumerable<ReportPiece>)[
                                                                           victim.Length == 0 ? ReportPiece.Plain(_text.Unknown) : ReportPiece.Code(victim),
                                                                           ReportPiece.Plain($" ({Number(counts[victim])})")
                                                                       ]));
    }

    /// <summary>
    /// Lists the growth candidates of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <returns>The pieces; unknown when the growth is not known, none when it is known and empty</returns>
    private List<ReportPiece> Growth(IncidentHypothesis hypothesis)
    {
        if (hypothesis.GrowthKnown)
        {
            return hypothesis.Growth.Count == 0
                       ? [ReportPiece.Plain(_text.None)]
                       : Joined(hypothesis.Growth.Select(growth => (IEnumerable<ReportPiece>) [ReportPiece.Code(growth.Program), ReportPiece.Plain($" +{Mib(growth.GrowthBytes)} MiB")]));
        }

        return [ReportPiece.Plain(_text.Unknown)];
    }

    /// <summary>
    /// Lists the largest consumers of a hypothesis.
    /// </summary>
    /// <param name="hypothesis">The hypothesis</param>
    /// <returns>The pieces; unknown when the incident had no snapshot</returns>
    private List<ReportPiece> Consumers(IncidentHypothesis hypothesis)
    {
        if (hypothesis.SnapshotCount == 0)
        {
            return [ReportPiece.Plain(_text.Unknown)];
        }

        if (hypothesis.Consumers.Count == 0)
        {
            return [ReportPiece.Plain(_text.None)];
        }

        return Joined(hypothesis.Consumers.Select(usage => (IEnumerable<ReportPiece>) [ReportPiece.Code(usage.Program), ReportPiece.Plain($" {Mib(usage.RssBytes)} MiB")]));
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
    /// Adds the head: the title and the facts about the data.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    private void AddHead(List<ReportBlock> blocks, ForensicsSummary summary)
    {
        blocks.Add(Heading(1, _text.Title));
        blocks.Add(Labeled(_text.GeneratedAt, Time(summary.GeneratedAt)));
        blocks.Add(Labeled(_text.LogLines, Counted(summary.LogLines, summary.FirstLogLineAt, summary.LastLogLineAt)));
        blocks.Add(Labeled(_text.ObservedHours, Number(summary.ObservedHours)));
        blocks.Add(Labeled(_text.Snapshots, Counted(summary.Snapshots, summary.FirstSnapshotAt, summary.LastSnapshotAt)));
        blocks.Add(Labeled(_text.Lookback, Number((long)summary.Lookback.TotalMinutes)));
        blocks.Add(Item(_text.AllTimesUtc));
    }

    /// <summary>
    /// Adds the summary: the counts, one line per detected pattern and the rules without a result.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    private void AddSummary(List<ReportBlock> blocks, ForensicsSummary summary)
    {
        var patterns = DetectedPatterns(summary);
        var unknown = UnknownRules(summary);

        blocks.Add(Heading(2, _text.SummaryHeading));
        blocks.Add(Item(Format(_text.Outages, Number(summary.Incidents), Number(summary.Kills))));

        if (summary.FirstOutageAt is not null)
        {
            blocks.Add(Item(Format(_text.FirstOutage, Time(summary.FirstOutageAt))));
            blocks.Add(Item(Format(_text.LastOutage, Time(summary.LastOutageAt))));
        }

        blocks.AddRange(patterns.Select(pattern => Item(pattern)));

        if (unknown.Count > 0)
        {
            blocks.Add(Item(Format(_text.UnknownRules, string.Join(", ", unknown))));
        }

        if (patterns.Count == 0)
        {
            blocks.Add(Item(unknown.Count == 0 ? _text.NoPattern : _text.NoPatternAmongDecided));
        }
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
    /// Adds the recurring patterns: the hours of the day, the activities and the panel threshold.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    private void AddPatterns(List<ReportBlock> blocks, ForensicsSummary summary)
    {
        blocks.Add(Heading(2, _text.PatternsHeading));
        blocks.Add(Heading(3, _text.TimeOfDayHeading));
        AddHours(blocks, summary);
        blocks.Add(Heading(3, _text.ActivitiesHeading));
        blocks.Add(Table([false, true, true, true, true, false, false],
                         [_text.Activity, _text.OutagesWith, _text.Known, _text.OutageShare, _text.BaseShare, _text.Result, _text.Reason],
                         ActivityRow(_text.Backup, summary.Backup),
                         ActivityRow(_text.Boot, summary.Boot),
                         ActivityRow(_text.PanelHigh, summary.PanelHigh)));
        blocks.Add(Paragraph(Format(_text.PanelThreshold, summary.PanelThresholdBytes == 0 ? _text.None : Mib(summary.PanelThresholdBytes) + " MiB")));
    }

    /// <summary>
    /// Adds the table of the hours of the day that have an outage, or the text that says why there is none.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    private void AddHours(List<ReportBlock> blocks, ForensicsSummary summary)
    {
        var unknown = summary.TimeOfDay.FirstOrDefault(result => result.Outcome == PatternOutcome.Unknown);

        if (summary.TimeOfDay.Count == 0 || unknown is not null)
        {
            blocks.Add(Paragraph($"{_text.Result}: {_text.Unknown} ({ReasonText(unknown?.Reason ?? UnknownReason.None)})"));

            return;
        }

        var hours = Enumerable.Range(0, summary.TimeOfDay.Count).Where(hour => summary.TimeOfDay[hour].Matching > 0).ToList();

        if (hours.Count == 0)
        {
            blocks.Add(Paragraph(_text.None));

            return;
        }

        var rows = new List<IReadOnlyList<string>>
                   {
                       new[] { _text.Hour, _text.OutageCount, _text.OutageShare, _text.BaseShare, _text.Result }
                   };

        rows.AddRange(hours.Select(hour => (IReadOnlyList<string>)[
                                                                      HourRange(hour),
                                                                      Number(summary.TimeOfDay[hour].Matching),
                                                                      Percent(summary.TimeOfDay[hour].Share),
                                                                      Percent(summary.TimeOfDay[hour].BaseShare),
                                                                      Word(summary.TimeOfDay[hour].Outcome)
                                                                  ]));
        blocks.Add(Table([false, true, true, true, false], rows.ToArray()));
    }

    /// <summary>
    /// Creates the row of an activity.
    /// </summary>
    /// <param name="name">The name of the activity</param>
    /// <param name="result">The result of its rule</param>
    /// <returns>The cells</returns>
    private IReadOnlyList<string> ActivityRow(string name, PatternResult result)
    {
        return [name, Number(result.Matching), Number(result.Known), Percent(result.Share), Percent(result.BaseShare), Word(result.Outcome), ReasonText(result.Reason)];
    }

    /// <summary>
    /// Adds the section of the new listeners.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="listeners">The section</param>
    private void AddListeners(List<ReportBlock> blocks, ListenerReport listeners)
    {
        blocks.Add(Heading(2, _text.NewListenersHeading));

        if (listeners.Agents.Count == 0)
        {
            blocks.Add(Paragraph(_text.NoAgentSnapshots));

            return;
        }

        blocks.AddRange(listeners.Agents.Select(tally => Item(0,
                                                              WithCode(_text.ListenerAgentHead,
                                                                       tally.AgentId,
                                                                       Number(tally.Snapshots),
                                                                       Time(tally.FirstAt),
                                                                       Time(tally.LastAt),
                                                                       Number(tally.Starts),
                                                                       Number(tally.ComparedAll),
                                                                       Number(tally.ComparedPrivileged),
                                                                       Number(tally.NotCompared)).ToArray())));

        var starts = Ordered(listeners.StartFindings);
        var found = Ordered(listeners.AgentFindings);

        foreach (var finding in starts)
        {
            AddFinding(blocks, finding, StartText(finding));
        }

        foreach (var finding in found)
        {
            AddFinding(blocks, finding, WithCode(_text.ListenerAgentFinding, finding.AgentId, Time(finding.CapturedAt), PortsText(finding.Outcome), Number(finding.Listeners.Count + finding.Omitted)));
        }

        var anyNew = starts.Concat(found).Any(finding => IsCompared(finding.Outcome));

        if (anyNew)
        {
            return;
        }

        var notCompared = listeners.Agents.Any(tally => tally.NotCompared > 0) || starts.Any(finding => finding.Outcome is not (ListenerOutcome.AllPorts or ListenerOutcome.PrivilegedOnly));

        blocks.Add(Item(notCompared ? _text.NoNewListenerCompared : _text.NoNewListener));
    }

    /// <summary>
    /// Adds the notes of the caps of the listener section.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="listeners">The section</param>
    private void AddListenerNotes(List<ReportBlock> blocks, ListenerReport listeners)
    {
        if (listeners.StartFindingsTotal > listeners.StartFindings.Count)
        {
            blocks.Add(Item(Format(_text.CapStartFindingsNote, Number(ListenerReport.MaxFindings), Number(listeners.StartFindingsTotal - listeners.StartFindings.Count))));
        }

        if (listeners.AgentFindingsTotal > listeners.AgentFindings.Count)
        {
            blocks.Add(Item(Format(_text.CapAgentFindingsNote, Number(ListenerReport.MaxFindings), Number(listeners.AgentFindingsTotal - listeners.AgentFindings.Count))));
        }

        if (listeners.AgentsOmitted > 0)
        {
            blocks.Add(Item(Format(_text.CapAgentsNote, Number(ListenerReport.MaxAgents), Number(listeners.AgentsOmitted))));
        }
    }

    /// <summary>
    /// Adds a finding and its listeners.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="finding">The finding</param>
    /// <param name="pieces">The pieces of the finding's line</param>
    private void AddFinding(List<ReportBlock> blocks, ListenerFinding finding, List<ReportPiece> pieces)
    {
        blocks.Add(Item(0, pieces.ToArray()));

        if (IsCompared(finding.Outcome))
        {
            blocks.AddRange(finding.Listeners.Select(listener => Item(1, ListenerPieces(listener).ToArray())));

            if (finding.Omitted > 0)
            {
                blocks.Add(Item(1, ReportPiece.Plain(Format(_text.ListenerRest, Number(finding.Omitted)))));
            }
        }
    }

    /// <summary>
    /// Writes the line of a finding after an agent start.
    /// </summary>
    /// <param name="finding">The finding</param>
    /// <returns>The pieces</returns>
    private List<ReportPiece> StartText(ListenerFinding finding)
    {
        if (IsCompared(finding.Outcome))
        {
            return WithCode(_text.ListenerStartFinding,
                            finding.AgentId,
                            Time(finding.CapturedAt),
                            Time(finding.BaseCapturedAt),
                            PortsText(finding.Outcome),
                            Number(finding.Listeners.Count + finding.Omitted));
        }

        var reason = finding.Outcome switch
                     {
                         ListenerOutcome.NoEarlierInventory => _text.ListenerNoEarlier,
                         ListenerOutcome.PrivilegedIncomplete => _text.ListenerPrivilegedIncomplete,
                         _ => _text.ListenerNotComparable
                     };

        return WithCode(_text.ListenerStartNotCompared, finding.AgentId, Time(finding.CapturedAt), reason);
    }

    /// <summary>
    /// Writes the line of a listener with its owner.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <returns>The pieces</returns>
    private List<ReportPiece> ListenerPieces(Listener listener)
    {
        var endpoint = $"{listener.Proto} {listener.Local}";

        return listener.Pid == 0
                   ? WithCode(_text.ListenerEntryNoOwner, string.Empty, endpoint)
                   : WithCode(_text.ListenerEntry, listener.Command, endpoint, Number(listener.Pid));
    }

    /// <summary>
    /// Returns the label of the ports a comparison covered.
    /// </summary>
    /// <param name="outcome">The outcome</param>
    /// <returns>The label</returns>
    private string PortsText(ListenerOutcome outcome)
    {
        return outcome == ListenerOutcome.PrivilegedOnly ? _text.ListenerPrivilegedOnly : _text.ListenerAllPorts;
    }

    /// <summary>
    /// Adds the permanent and transient consumers.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    private void AddConsumers(List<ReportBlock> blocks, ForensicsSummary summary)
    {
        blocks.Add(Heading(2, _text.MemoryHeading));
        blocks.Add(Heading(3, _text.PermanentHeading));
        AddConsumerList(blocks,
                        summary,
                        summary.Permanent.Select(consumer => Item(0,
                                                                  ReportPiece.Code(consumer.Program),
                                                                  ReportPiece.Plain(Format(_text.ConsumerPermanent, string.Empty, Percent(consumer.Share), Mib(consumer.MeanRssBytes), Mib(consumer.PeakRssBytes))))));
        blocks.Add(Heading(3, _text.TransientHeading));
        AddConsumerList(blocks,
                        summary,
                        summary.Transient.Select(consumer => Item(0,
                                                                  ReportPiece.Code(consumer.Program),
                                                                  ReportPiece.Plain(Format(_text.ConsumerTransient, string.Empty, Percent(consumer.Share), Mib(consumer.PeakRssBytes), Time(consumer.PeakAt))))));
    }

    /// <summary>
    /// Adds a list of consumers, or the text that says why there is none.
    /// </summary>
    /// <param name="blocks">The blocks</param>
    /// <param name="summary">The summary</param>
    /// <param name="items">The items of the consumers</param>
    private void AddConsumerList(List<ReportBlock> blocks, ForensicsSummary summary, IEnumerable<ReportBlock> items)
    {
        var list = items.ToList();

        if (summary.Snapshots == 0)
        {
            blocks.Add(Paragraph(_text.NoSnapshots));
        }
        else if (list.Count == 0)
        {
            blocks.Add(Paragraph(_text.None));
        }
        else
        {
            blocks.AddRange(list);
        }
    }

    #endregion // Methods
}