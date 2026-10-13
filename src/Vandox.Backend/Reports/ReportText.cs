namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The labels of the forensics report in one language.
/// </summary>
public sealed class ReportText
{
    #region Properties

    /// <summary>
    /// Gets the English table.
    /// </summary>
    public static ReportText English => throw new NotImplementedException();

    /// <summary>
    /// Gets the German table.
    /// </summary>
    public static ReportText German => throw new NotImplementedException();

    /// <summary>
    /// Gets the language codes, <c>en</c> and <c>de</c>.
    /// </summary>
    public static IReadOnlyList<string> Languages => throw new NotImplementedException();

    /// <summary>
    /// Gets the language code of this table.
    /// </summary>
    public string Language { get; init; } = string.Empty;

    /// <summary>
    /// Gets the decimal separator of this language.
    /// </summary>
    public char DecimalSeparator { get; init; }

    /// <summary>
    /// Gets the title heading.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary heading.
    /// </summary>
    public string SummaryHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the recurring patterns heading.
    /// </summary>
    public string PatternsHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the time of day heading.
    /// </summary>
    public string TimeOfDayHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the activities heading.
    /// </summary>
    public string ActivitiesHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the memory consumers heading.
    /// </summary>
    public string MemoryHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the permanent consumers heading.
    /// </summary>
    public string PermanentHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the transient consumers heading.
    /// </summary>
    public string TransientHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outages heading.
    /// </summary>
    public string OutagesHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the heading of one outage, with the number as {0} and the time as {1}.
    /// </summary>
    public string OutageHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the notes heading.
    /// </summary>
    public string NotesHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for a detected rule.
    /// </summary>
    public string Detected { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for a rule that found no pattern.
    /// </summary>
    public string NotDetected { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for an unknown result.
    /// </summary>
    public string Unknown { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for yes.
    /// </summary>
    public string Yes { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for no.
    /// </summary>
    public string No { get; init; } = string.Empty;

    /// <summary>
    /// Gets the word for none.
    /// </summary>
    public string None { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: generation time.
    /// </summary>
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: log lines.
    /// </summary>
    public string LogLines { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: observed hours.
    /// </summary>
    public string ObservedHours { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: process snapshots.
    /// </summary>
    public string Snapshots { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: lookback in minutes.
    /// </summary>
    public string Lookback { get; init; } = string.Empty;

    /// <summary>
    /// Gets the head line: all times are UTC.
    /// </summary>
    public string AllTimesUtc { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: outage and kill counts.
    /// </summary>
    public string Outages { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: first outage.
    /// </summary>
    public string FirstOutage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: last outage.
    /// </summary>
    public string LastOutage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: one detected pattern.
    /// </summary>
    public string PatternLine { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: rules that are unknown.
    /// </summary>
    public string UnknownRules { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: no pattern, every rule decided.
    /// </summary>
    public string NoPattern { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: no pattern among the decided rules.
    /// </summary>
    public string NoPatternAmongDecided { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: hour.
    /// </summary>
    public string Hour { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: outages.
    /// </summary>
    public string OutageCount { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: share of outages.
    /// </summary>
    public string OutageShare { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: share of observed hours.
    /// </summary>
    public string BaseShare { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: result.
    /// </summary>
    public string Result { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: activity.
    /// </summary>
    public string Activity { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: outages known.
    /// </summary>
    public string Known { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: reason.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Gets the line: panel memory threshold.
    /// </summary>
    public string PanelThreshold { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the backup activity.
    /// </summary>
    public string Backup { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the boot activity.
    /// </summary>
    public string Boot { get; init; } = string.Empty;

    /// <summary>
    /// Gets the name of the panel memory activity.
    /// </summary>
    public string PanelHigh { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: too few outages.
    /// </summary>
    public string ReasonTooFewIncidents { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: no observed hour.
    /// </summary>
    public string ReasonNoBaseData { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: too many snapshots.
    /// </summary>
    public string ReasonTooManySnapshots { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: too many boots.
    /// </summary>
    public string ReasonTooManyBoots { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: too many observed runs.
    /// </summary>
    public string ReasonTooManyObservedRuns { get; init; } = string.Empty;

    /// <summary>
    /// Gets the reason: no panel memory.
    /// </summary>
    public string ReasonNoPanelMemory { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: window.
    /// </summary>
    public string Window { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: kill count.
    /// </summary>
    public string Kills { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: victims.
    /// </summary>
    public string Victims { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: kills not kept.
    /// </summary>
    public string KillsNotKept { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: growth candidates.
    /// </summary>
    public string Growth { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: largest consumers.
    /// </summary>
    public string Consumers { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: activities.
    /// </summary>
    public string Activities { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: service events.
    /// </summary>
    public string ServiceEvents { get; init; } = string.Empty;

    /// <summary>
    /// Gets the outage line: time of the last boot.
    /// </summary>
    public string LastBoot { get; init; } = string.Empty;

    /// <summary>
    /// Gets the consumer line of a permanent consumer.
    /// </summary>
    public string ConsumerPermanent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the consumer line of a transient consumer.
    /// </summary>
    public string ConsumerTransient { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: no process snapshot.
    /// </summary>
    public string NoSnapshots { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: no outage.
    /// </summary>
    public string NoOutages { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: the rules.
    /// </summary>
    public string RulesNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: a cap was hit.
    /// </summary>
    public string CapNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: program entries not tracked.
    /// </summary>
    public string UntrackedNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: the incident count changed between the passes.
    /// </summary>
    public string IncidentsChangedNote { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the table of a language.
    /// </summary>
    /// <param name="language">The language code, compared ordinally with <c>en</c> and <c>de</c></param>
    /// <returns>The table; <c>null</c> for any other value</returns>
    public static ReportText? For(string language)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}