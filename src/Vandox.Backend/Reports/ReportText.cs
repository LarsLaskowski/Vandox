namespace Vandox.Backend.Reports;

/// <summary>
/// The labels of the forensics report in one language.
/// </summary>
public sealed class ReportText
{
    #region Properties

    /// <summary>
    /// Gets the English table.
    /// </summary>
    public static ReportText English { get; } = new()
                                                {
                                                    Language = "en",
                                                    DecimalSeparator = '.',
                                                    Title = "Vandox forensics report",
                                                    SummaryHeading = "Summary",
                                                    PatternsHeading = "Recurring patterns",
                                                    TimeOfDayHeading = "Time of day (UTC)",
                                                    ActivitiesHeading = "Activities before the outages",
                                                    MemoryHeading = "Memory consumers",
                                                    PermanentHeading = "Permanent consumers",
                                                    TransientHeading = "Transient consumers",
                                                    OutagesHeading = "Outages",
                                                    OutageHeading = "Outage #{0} at {1}",
                                                    NotesHeading = "Notes",
                                                    Detected = "detected",
                                                    NotDetected = "not detected",
                                                    Unknown = "unknown",
                                                    Yes = "yes",
                                                    No = "no",
                                                    None = "none",
                                                    GeneratedAt = "Generated",
                                                    LogLines = "Log lines",
                                                    ObservedHours = "Observed hours",
                                                    Snapshots = "Process snapshots",
                                                    Lookback = "Lookback in minutes",
                                                    AllTimesUtc = "All times are UTC.",
                                                    CountWithRange = "{0} (first {1}, last {2})",
                                                    Outages = "Outages: {0}, OOM kills: {1}",
                                                    FirstOutage = "First outage: {0}",
                                                    LastOutage = "Last outage: {0}",
                                                    PatternLine = "{0}: {1} of {2} outages ({3}) against {4} of the observed hours",
                                                    UnknownRules = "Rules without a result: {0}",
                                                    NoPattern = "No recurring pattern detected",
                                                    NoPatternAmongDecided = "No recurring pattern detected among the decided rules",
                                                    TimeOfDay = "Time of day",
                                                    Hour = "Hour",
                                                    OutageCount = "Outages",
                                                    OutageShare = "Share of outages",
                                                    BaseShare = "Share of observed hours",
                                                    Result = "Result",
                                                    Activity = "Activity",
                                                    OutagesWith = "Outages with it",
                                                    Known = "Outages known",
                                                    Reason = "Reason",
                                                    PanelThreshold = "Panel memory threshold: {0}",
                                                    Backup = "Backup running",
                                                    Boot = "Boot",
                                                    PanelHigh = "Panel memory high",
                                                    ReasonTooFewIncidents = "too few outages",
                                                    ReasonNoBaseData = "no observed hour",
                                                    ReasonTooManySnapshots = "too many process snapshots",
                                                    ReasonTooManyBoots = "too many boots",
                                                    ReasonTooManyObservedRuns = "too many runs of observed hours",
                                                    ReasonNoPanelMemory = "no panel memory",
                                                    Window = "Window",
                                                    Kills = "Kills",
                                                    Victims = "Victims",
                                                    KillsNotKept = "Kills not kept",
                                                    Growth = "Growth candidates",
                                                    Consumers = "Largest consumers",
                                                    Activities = "Activities before the outage",
                                                    ServiceEvents = "Service events in the lookback",
                                                    LastBoot = "last boot {0}",
                                                    ConsumerPermanent = "{0}: in {1} of the snapshots, mean {2} MiB, peak {3} MiB",
                                                    ConsumerTransient = "{0}: in {1} of the snapshots, peak {2} MiB at {3}",
                                                    NoSnapshots = "No process snapshot is stored, so the consumers are unknown.",
                                                    NoOutages = "No outage is stored; run vandoxd incidents first.",
                                                    RulesNote = "A pattern is reported when at least 2 of at least 3 known outages share a property, their share is above the share of the observed hours and the probability of that many by chance is at most 1 % (1 % / 24 for one hour of the day). Observed hours are UTC hours with a line of the system log (journal or syslog); an activity is judged over the lookback before the first kill. A rule that cannot be decided is shown as unknown with its reason, never as not detected.",
                                                    CapSnapshotsNote = "The report keeps at most {0} process snapshot points; the later snapshots were not used, so backup and panel memory are unknown.",
                                                    CapBootsNote = "The report keeps at most {0} boots; the later boots were not used, so the boot rule is unknown.",
                                                    CapRunsNote = "The report keeps at most {0} runs of consecutive observed hours; the later hours were not used, so backup, boot and panel memory are unknown.",
                                                    UntrackedNote = "{0} program entries were not tracked because the report tracks at most {1} programs.",
                                                    IncidentsChangedNote = "The number of incidents changed between the two passes over the stored incidents, so the rules and the list of outages may not match; run vandoxd incidents and the report again.",
                                                    NewListenersHeading = "New listeners",
                                                    ListenerAllPorts = "all ports",
                                                    ListenerPrivilegedOnly = "ports 1 to 1023 only",
                                                    ListenerNoEarlier = "no earlier inventory",
                                                    ListenerPrivilegedIncomplete = "ports 1 to 1023 not complete",
                                                    ListenerNotComparable = "inventories not comparable",
                                                    ListenerRest = "and {0} more",
                                                    NoNewListener = "No new listener found.",
                                                    NoNewListenerCompared = "No new listener found among the compared inventories.",
                                                    NoAgentSnapshots = "No agent connection snapshot stored: new listeners unknown.",
                                                    ListenerAgentHead = "Agent {0}: {1} connection snapshots from {2} to {3}; {4} agent starts: {5} compared on all ports, {6} on ports 1 to 1023 only, {7} not compared",
                                                    ListenerStartFinding = "{1}, agent {0}, after an agent start (compared with {2}), {3}: {4} new",
                                                    ListenerStartNotCompared = "{1}, agent {0}, after an agent start, not compared: {2}",
                                                    ListenerAgentFinding = "{1}, agent {0}, found by the agent, {2}: {3} new",
                                                    ListenerEntry = "{1}, PID {2} {0}",
                                                    ListenerEntryNoOwner = "{1}, no owner",
                                                    CapStartFindingsNote = "Only the latest {0} findings after an agent start are listed; {1} earlier ones are left out.",
                                                    CapAgentFindingsNote = "Only the latest {0} findings of the agent are listed; {1} earlier ones are left out.",
                                                    CapAgentsNote = "Only the first {0} agent IDs are listed; {1} more are left out.",
                                                };

    /// <summary>
    /// Gets the German table.
    /// </summary>
    public static ReportText German { get; } = new()
                                               {
                                                   Language = "de",
                                                   DecimalSeparator = ',',
                                                   Title = "Vandox-Forensikbericht",
                                                   SummaryHeading = "Übersicht",
                                                   PatternsHeading = "Wiederkehrende Muster",
                                                   TimeOfDayHeading = "Tageszeit (UTC)",
                                                   ActivitiesHeading = "Aktivitäten vor den Ausfällen",
                                                   MemoryHeading = "Speicherverbraucher",
                                                   PermanentHeading = "Dauerhafte Verbraucher",
                                                   TransientHeading = "Vorübergehende Verbraucher",
                                                   OutagesHeading = "Ausfälle",
                                                   OutageHeading = "Ausfall #{0} am {1}",
                                                   NotesHeading = "Hinweise",
                                                   Detected = "erkannt",
                                                   NotDetected = "nicht erkannt",
                                                   Unknown = "unbekannt",
                                                   Yes = "ja",
                                                   No = "nein",
                                                   None = "keine",
                                                   GeneratedAt = "Erstellt",
                                                   LogLines = "Logzeilen",
                                                   ObservedHours = "Beobachtete Stunden",
                                                   Snapshots = "Prozess-Snapshots",
                                                   Lookback = "Rückblick in Minuten",
                                                   AllTimesUtc = "Alle Zeiten sind UTC.",
                                                   CountWithRange = "{0} (erste {1}, letzte {2})",
                                                   Outages = "Ausfälle: {0}, OOM-Kills: {1}",
                                                   FirstOutage = "Erster Ausfall: {0}",
                                                   LastOutage = "Letzter Ausfall: {0}",
                                                   PatternLine = "{0}: {1} von {2} Ausfällen ({3}) gegenüber {4} der beobachteten Stunden",
                                                   UnknownRules = "Regeln ohne Ergebnis: {0}",
                                                   NoPattern = "Kein wiederkehrendes Muster erkannt",
                                                   NoPatternAmongDecided = "Kein wiederkehrendes Muster unter den entschiedenen Regeln erkannt",
                                                   TimeOfDay = "Tageszeit",
                                                   Hour = "Stunde",
                                                   OutageCount = "Ausfälle",
                                                   OutageShare = "Anteil der Ausfälle",
                                                   BaseShare = "Anteil der beobachteten Stunden",
                                                   Result = "Ergebnis",
                                                   Activity = "Aktivität",
                                                   OutagesWith = "Ausfälle damit",
                                                   Known = "Ausfälle bekannt",
                                                   Reason = "Grund",
                                                   PanelThreshold = "Schwelle für hohen Panel-Speicher: {0}",
                                                   Backup = "Backup läuft",
                                                   Boot = "Neustart",
                                                   PanelHigh = "Panel-Speicher hoch",
                                                   ReasonTooFewIncidents = "zu wenige Ausfälle",
                                                   ReasonNoBaseData = "keine beobachtete Stunde",
                                                   ReasonTooManySnapshots = "zu viele Prozess-Snapshots",
                                                   ReasonTooManyBoots = "zu viele Neustarts",
                                                   ReasonTooManyObservedRuns = "zu viele Abschnitte beobachteter Stunden",
                                                   ReasonNoPanelMemory = "kein Panel-Speicher",
                                                   Window = "Zeitfenster",
                                                   Kills = "Kills",
                                                   Victims = "Opfer",
                                                   KillsNotKept = "Nicht gespeicherte Kills",
                                                   Growth = "Wachstumskandidaten",
                                                   Consumers = "Größte Verbraucher",
                                                   Activities = "Aktivitäten vor dem Ausfall",
                                                   ServiceEvents = "Dienstereignisse im Rückblick",
                                                   LastBoot = "letzter Neustart {0}",
                                                   ConsumerPermanent = "{0}: in {1} der Snapshots, Mittel {2} MiB, Spitze {3} MiB",
                                                   ConsumerTransient = "{0}: in {1} der Snapshots, Spitze {2} MiB am {3}",
                                                   NoSnapshots = "Es ist kein Prozess-Snapshot gespeichert, daher sind die Verbraucher unbekannt.",
                                                   NoOutages = "Es ist kein Ausfall gespeichert; zuerst vandoxd incidents ausführen.",
                                                   RulesNote = "Ein Muster wird gemeldet, wenn mindestens 2 von mindestens 3 bekannten Ausfällen eine Eigenschaft teilen, ihr Anteil über dem Anteil der beobachteten Stunden liegt und die Wahrscheinlichkeit für so viele Ausfälle durch Zufall höchstens 1 % beträgt (1 % / 24 für eine Stunde des Tages). Beobachtete Stunden sind UTC-Stunden mit einer Zeile des Systemlogs (journal oder syslog); eine Aktivität wird über den Rückblick vor dem ersten Kill beurteilt. Eine Regel, die nicht entschieden werden kann, steht als unbekannt mit ihrem Grund da, nie als nicht erkannt.",
                                                   CapSnapshotsNote = "Der Bericht behält höchstens {0} Prozess-Snapshot-Punkte; die späteren Snapshots wurden nicht verwendet, daher sind Backup und Panel-Speicher unbekannt.",
                                                   CapBootsNote = "Der Bericht behält höchstens {0} Neustarts; die späteren Neustarts wurden nicht verwendet, daher ist die Neustart-Regel unbekannt.",
                                                   CapRunsNote = "Der Bericht behält höchstens {0} Abschnitte aufeinanderfolgender beobachteter Stunden; die späteren Stunden wurden nicht verwendet, daher sind Backup, Neustart und Panel-Speicher unbekannt.",
                                                   UntrackedNote = "{0} Programmeinträge wurden nicht verfolgt, weil der Bericht höchstens {1} Programme verfolgt.",
                                                   IncidentsChangedNote = "Die Zahl der Vorfälle hat sich zwischen den beiden Durchläufen über die gespeicherten Vorfälle geändert, daher passen Regeln und Ausfallliste möglicherweise nicht zusammen; vandoxd incidents und den Bericht erneut ausführen.",
                                                   NewListenersHeading = "Neue Listener",
                                                   ListenerAllPorts = "alle Ports",
                                                   ListenerPrivilegedOnly = "nur Ports 1 bis 1023",
                                                   ListenerNoEarlier = "kein früheres Inventar",
                                                   ListenerPrivilegedIncomplete = "Ports 1 bis 1023 unvollständig",
                                                   ListenerNotComparable = "Inventare nicht vergleichbar",
                                                   ListenerRest = "und {0} weitere",
                                                   NoNewListener = "Kein neuer Listener gefunden.",
                                                   NoNewListenerCompared = "Kein neuer Listener unter den verglichenen Inventaren gefunden.",
                                                   NoAgentSnapshots = "Kein Verbindungs-Snapshot eines Agenten gespeichert: neue Listener unbekannt.",
                                                   ListenerAgentHead = "Agent {0}: {1} Verbindungs-Snapshots von {2} bis {3}; {4} Agentenstarts: {5} auf allen Ports verglichen, {6} nur auf Ports 1 bis 1023, {7} nicht verglichen",
                                                   ListenerStartFinding = "{1}, Agent {0}, nach einem Agentenstart (verglichen mit {2}), {3}: {4} neu",
                                                   ListenerStartNotCompared = "{1}, Agent {0}, nach einem Agentenstart, nicht verglichen: {2}",
                                                   ListenerAgentFinding = "{1}, Agent {0}, vom Agenten gefunden, {2}: {3} neu",
                                                   ListenerEntry = "{1}, PID {2} {0}",
                                                   ListenerEntryNoOwner = "{1}, kein Besitzer",
                                                   CapStartFindingsNote = "Nur die letzten {0} Funde nach einem Agentenstart sind aufgeführt; {1} frühere fehlen.",
                                                   CapAgentFindingsNote = "Nur die letzten {0} Funde des Agenten sind aufgeführt; {1} frühere fehlen.",
                                                   CapAgentsNote = "Nur die ersten {0} Agenten-IDs sind aufgeführt; {1} weitere fehlen.",
                                               };

    /// <summary>
    /// Gets the language codes, <c>en</c> and <c>de</c>.
    /// </summary>
    public static IReadOnlyList<string> Languages { get; } = ["en", "de"];

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
    /// Gets the head text of a count with its first time as {1} and last time as {2}, the count as {0}.
    /// </summary>
    public string CountWithRange { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: outage and kill counts, with the outages as {0} and the kills as {1}.
    /// </summary>
    public string Outages { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: first outage, with the time as {0}.
    /// </summary>
    public string FirstOutage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: last outage, with the time as {0}.
    /// </summary>
    public string LastOutage { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line of one detected pattern: the rule as {0}, the outages with it as {1}, the known outages as {2}, their share as {3} and the share of observed hours as {4}.
    /// </summary>
    public string PatternLine { get; init; } = string.Empty;

    /// <summary>
    /// Gets the summary line: the rules without a result, as {0}.
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
    /// Gets the name of the time of day rule.
    /// </summary>
    public string TimeOfDay { get; init; } = string.Empty;

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
    /// Gets the table column: outages with the activity.
    /// </summary>
    public string OutagesWith { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: outages known.
    /// </summary>
    public string Known { get; init; } = string.Empty;

    /// <summary>
    /// Gets the table column: reason.
    /// </summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>
    /// Gets the line: panel memory threshold, with the value as {0}.
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
    /// Gets the text of the last boot, with the time as {0}.
    /// </summary>
    public string LastBoot { get; init; } = string.Empty;

    /// <summary>
    /// Gets the consumer line of a permanent consumer: the program as {0}, the share of snapshots as {1}, the mean as {2} and the peak as {3} in MiB.
    /// </summary>
    public string ConsumerPermanent { get; init; } = string.Empty;

    /// <summary>
    /// Gets the consumer line of a transient consumer: the program as {0}, the share of snapshots as {1}, the peak in MiB as {2} and its time as {3}.
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
    /// Gets the note: the snapshot cap was hit, with the cap as {0}.
    /// </summary>
    public string CapSnapshotsNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: the boot cap was hit, with the cap as {0}.
    /// </summary>
    public string CapBootsNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: the cap of runs of observed hours was hit, with the cap as {0}.
    /// </summary>
    public string CapRunsNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: program entries not tracked, with the entries as {0} and the cap as {1}.
    /// </summary>
    public string UntrackedNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the note: the incident count changed between the passes.
    /// </summary>
    public string IncidentsChangedNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the heading of the new listeners section.
    /// </summary>
    public string NewListenersHeading { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the label of a comparison on all ports.
    /// </summary>
    public string ListenerAllPorts { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the label of a comparison on ports 1 to 1023 only.
    /// </summary>
    public string ListenerPrivilegedOnly { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the reason: no earlier inventory.
    /// </summary>
    public string ListenerNoEarlier { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the reason: the privileged inventory was incomplete.
    /// </summary>
    public string ListenerPrivilegedIncomplete { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the reason: the inventories are not comparable.
    /// </summary>
    public string ListenerNotComparable { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the line for the listeners left out of a finding.
    /// </summary>
    public string ListenerRest { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the line: no new listener found.
    /// </summary>
    public string NoNewListener { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the line: no new listener among the compared inventories.
    /// </summary>
    public string NoNewListenerCompared { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the line: no connection snapshots of any agent.
    /// </summary>
    public string NoAgentSnapshots { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the head line of an agent.
    /// </summary>
    public string ListenerAgentHead { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the template of a finding after an agent start.
    /// </summary>
    public string ListenerStartFinding { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the template of an agent start that was not compared.
    /// </summary>
    public string ListenerStartNotCompared { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the template of a finding by the agent.
    /// </summary>
    public string ListenerAgentFinding { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the template of one listener with its owner.
    /// </summary>
    public string ListenerEntry { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the template of one listener without an owner.
    /// </summary>
    public string ListenerEntryNoOwner { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the note: only the latest findings after an agent start are listed.
    /// </summary>
    public string CapStartFindingsNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the note: only the latest findings of the agent are listed.
    /// </summary>
    public string CapAgentFindingsNote { get; init; } = string.Empty;

    /// <summary>
    /// Gets the text: the note: only the first agent IDs are listed.
    /// </summary>
    public string CapAgentsNote { get; init; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the table of a language.
    /// </summary>
    /// <param name="language">The language code, compared ordinally with <c>en</c> and <c>de</c></param>
    /// <returns>The table; <c>null</c> for any other value</returns>
    public static ReportText? For(string language)
    {
        return language switch
               {
                   "en" => English,
                   "de" => German,
                   _ => null
               };
    }

    #endregion // Methods
}