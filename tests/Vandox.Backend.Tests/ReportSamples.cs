using Vandox.Backend.Reports;
using Vandox.Core.Incidents;
using Vandox.Core.Reports;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// The hand-built summaries, outages and activities of the report tests: a rich sample that has every part of the report, and a sparse one
/// with the variants the rich one cannot show.
/// </summary>
internal static class ReportSamples
{
    #region Constants

    /// <summary>
    /// One MiB in bytes.
    /// </summary>
    internal const ulong Mib = 1048576;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time of the first kill of the sample outages.
    /// </summary>
    internal static readonly DateTimeOffset KillAt = new(2026, 10, 2, 8, 59, 59, TimeSpan.FromHours(2));

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates the rich summary: a detected hour with a second hour in the table, a detected backup, a boot and a panel that are unknown with their reasons,
    /// both consumer lists, all three caps, untracked programs and a count of incidents that differs from the outages of the sample.
    /// </summary>
    /// <returns>The summary</returns>
    internal static ForensicsSummary RichSummary()
    {
        var hours = new List<PatternResult>();

        for (var hour = 0; hour < 24; hour++)
        {
            hours.Add(hour switch
                      {
                          2 => new PatternResult
                               {
                                   Outcome = PatternOutcome.Detected,
                                   Matching = 3,
                                   Known = 13,
                                   BaseMatching = 1,
                                   BaseKnown = 24
                               },
                          5 => new PatternResult
                               {
                                   Outcome = PatternOutcome.NotDetected,
                                   Matching = 1,
                                   Known = 13,
                                   BaseMatching = 1,
                                   BaseKnown = 24
                               },
                          _ => Decided()
                      });
        }

        return Summary(hours,
                       new PatternResult
                       {
                           Outcome = PatternOutcome.Detected,
                           Matching = 3,
                           Known = 3,
                           BaseMatching = 3,
                           BaseKnown = 72
                       },
                       Unknown(UnknownReason.TooManyBoots),
                       Unknown(UnknownReason.NoPanelMemory),
                       permanent: [
                                      new ConsumerSummary
                                      {
                                          Program = "mariadbd",
                                          Snapshots = 2016,
                                          Share = 1,
                                          MeanRssBytes = 300 * Mib,
                                          PeakRssBytes = 400 * Mib,
                                          PeakAt = KillAt
                                      }
                                  ],
                       transient: [
                                      new ConsumerSummary
                                      {
                                          Program = "pzstd",
                                          Snapshots = 12,
                                          Share = 0.006,
                                          MeanRssBytes = 100 * Mib,
                                          PeakRssBytes = 150 * Mib,
                                          PeakAt = KillAt
                                      }
                                  ],
                       snapshotsTruncated: true,
                       bootsTruncated: true,
                       runsTruncated: true,
                       untracked: 7);
    }

    /// <summary>
    /// Creates the sparse summary: the time of day unknown (the paragraph instead of the table), no snapshot at all (the text instead of the consumer
    /// lists), no outage and no cap.
    /// </summary>
    /// <returns>The summary</returns>
    internal static ForensicsSummary SparseSummary()
    {
        return Summary(Undecided(UnknownReason.NoBaseData),
                       Unknown(UnknownReason.NoBaseData),
                       Decided(),
                       Unknown(UnknownReason.NoPanelMemory),
                       snapshots: 0,
                       incidents: 0);
    }

    /// <summary>
    /// Creates the outages of the rich sample: one with kills that were not kept, a duplicated and an unnamed victim, growth, consumers, a backup, a boot
    /// with its last time and service events; one without a snapshot (unknown growth and consumers), a boot that did not run and a victim that is a program of
    /// the consumers; one with a known but empty growth and no consumers.
    /// </summary>
    /// <returns>The stored incidents with their activities</returns>
    internal static IReadOnlyList<(StoredIncident Stored, IncidentActivities Activities)> RichOutages()
    {
        var first = Outage(KillAt, 5, "mariadbd");

        first.Kills = [Victim("mariadbd"), Victim("php-fpm"), Victim("mariadbd"), Victim(string.Empty)];
        first.Hypothesis = new IncidentHypothesis
                           {
                               SnapshotCount = 3,
                               GrowthKnown = true,
                               Growth = [
                                            new ProgramGrowth
                                            {
                                                Program = "pzstd",
                                                StartRssBytes = 100 * Mib,
                                                EndRssBytes = 868 * Mib,
                                                GrowthBytes = 768 * Mib
                                            },
                                            new ProgramGrowth
                                            {
                                                Program = "php-fpm",
                                                StartRssBytes = 10 * Mib,
                                                EndRssBytes = 12 * Mib,
                                                GrowthBytes = 2 * Mib
                                            }
                                        ],
                               Consumers = [
                                               new ProgramUsage
                                               {
                                                   Program = "apache2",
                                                   RssBytes = 400 * Mib,
                                                   Count = 4
                                               },
                                               new ProgramUsage
                                               {
                                                   Program = "mariadbd",
                                                   RssBytes = 300 * Mib,
                                                   Count = 1
                                               }
                                           ],
                               ServiceEvents = 2
                           };

        var second = Outage(KillAt.AddDays(1), 1, "nginx");
        var third = Outage(KillAt.AddDays(2), 1, "cron");

        third.Hypothesis = new IncidentHypothesis
                           {
                               SnapshotCount = 2,
                               GrowthKnown = true,
                               Growth = [],
                               Consumers = []
                           };

        return [
                   (Stored(7, first), Activities(Indicator.Yes, Indicator.Yes, Indicator.No, new DateTimeOffset(2026, 10, 2, 6, 30, 0, TimeSpan.Zero))),
                   (Stored(8, second), Activities(Indicator.No, Indicator.No, Indicator.Unknown)),
                   (Stored(9, third), Activities(Indicator.Unknown, Indicator.Unknown, Indicator.Yes))
               ];
    }

    /// <summary>
    /// Writes a whole report into a writer: the summary, the given outages in order, and the notes with the number of the outages as the count of the second pass.
    /// </summary>
    /// <param name="writer">The writer</param>
    /// <param name="summary">The summary</param>
    /// <param name="outages">The outages</param>
    /// <returns>A task that completes when the report is written</returns>
    internal static async Task WriteAsync(IReportWriter writer, ForensicsSummary summary, IReadOnlyList<(StoredIncident Stored, IncidentActivities Activities)> outages)
    {
        await writer.WriteSummaryAsync(summary);

        foreach (var (stored, activities) in outages)
        {
            await writer.WriteIncidentAsync(stored, activities);
        }

        await writer.WriteNotesAsync(summary, outages.Count);
    }

    /// <summary>
    /// Creates a decided result without a match.
    /// </summary>
    /// <returns>The result</returns>
    internal static PatternResult Decided()
    {
        return new PatternResult
               {
                   Outcome = PatternOutcome.NotDetected,
                   Matching = 0,
                   Known = 12,
                   BaseMatching = 2,
                   BaseKnown = 50
               };
    }

    /// <summary>
    /// Creates an unknown result with a reason.
    /// </summary>
    /// <param name="reason">The reason</param>
    /// <returns>The result</returns>
    internal static PatternResult Unknown(UnknownReason reason)
    {
        return new PatternResult
               {
                   Outcome = PatternOutcome.Unknown,
                   Reason = reason
               };
    }

    /// <summary>
    /// Creates the 24 results of the time of day.
    /// </summary>
    /// <param name="atTwo">The result of the hour 2</param>
    /// <param name="others">The result of the other hours; a decided one without a match when <c>null</c></param>
    /// <returns>The results</returns>
    internal static List<PatternResult> Hours(PatternResult atTwo, PatternResult? others = null)
    {
        var results = new List<PatternResult>();

        for (var hour = 0; hour < 24; hour++)
        {
            results.Add(hour == 2 ? atTwo : others ?? Decided());
        }

        return results;
    }

    /// <summary>
    /// Creates the 24 results of the time of day that are all unknown for one reason.
    /// </summary>
    /// <param name="reason">The reason</param>
    /// <returns>The results</returns>
    internal static List<PatternResult> Undecided(UnknownReason reason)
    {
        return Hours(new PatternResult
                     {
                         Outcome = PatternOutcome.Unknown,
                         Reason = reason
                     },
                     new PatternResult
                     {
                         Outcome = PatternOutcome.Unknown,
                         Reason = reason
                     });
    }

    /// <summary>
    /// Creates a summary with fixed counts and times.
    /// </summary>
    /// <param name="timeOfDay">The 24 results of the time of day</param>
    /// <param name="backup">The backup result</param>
    /// <param name="boot">The boot result</param>
    /// <param name="panel">The panel result</param>
    /// <param name="generatedAt">The generation time; a fixed one when <c>null</c></param>
    /// <param name="firstLogLineAt">The first log line; a fixed one when <c>null</c></param>
    /// <param name="lookback">The lookback; one hour when <c>null</c></param>
    /// <param name="panelThreshold">The panel threshold in bytes</param>
    /// <param name="permanent">The permanent consumers; none when <c>null</c></param>
    /// <param name="transient">The transient consumers; none when <c>null</c></param>
    /// <param name="snapshotsTruncated">Whether the snapshot cap was hit</param>
    /// <param name="bootsTruncated">Whether the boot cap was hit</param>
    /// <param name="runsTruncated">Whether the cap of runs of observed hours was hit</param>
    /// <param name="untracked">The number of program entries that were not tracked</param>
    /// <param name="snapshots">The number of snapshots</param>
    /// <param name="incidents">The number of incidents</param>
    /// <returns>The summary</returns>
    internal static ForensicsSummary Summary(IReadOnlyList<PatternResult> timeOfDay, PatternResult backup, PatternResult boot, PatternResult panel, DateTimeOffset? generatedAt = null, DateTimeOffset? firstLogLineAt = null, TimeSpan? lookback = null, ulong panelThreshold = 68 * Mib, IReadOnlyList<ConsumerSummary>? permanent = null, IReadOnlyList<ConsumerSummary>? transient = null, bool snapshotsTruncated = false, bool bootsTruncated = false, bool runsTruncated = false, long untracked = 0, long snapshots = 2016, long incidents = 13)
    {
        return new ForensicsSummary
               {
                   GeneratedAt = generatedAt ?? new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero),
                   Lookback = lookback ?? TimeSpan.FromHours(1),
                   LogLines = 123456,
                   FirstLogLineAt = firstLogLineAt ?? new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
                   LastLogLineAt = new DateTimeOffset(2026, 10, 6, 6, 0, 0, TimeSpan.Zero),
                   ObservedHours = 168,
                   Snapshots = snapshots,
                   FirstSnapshotAt = new DateTimeOffset(2026, 9, 29, 0, 5, 0, TimeSpan.Zero),
                   LastSnapshotAt = new DateTimeOffset(2026, 10, 6, 5, 55, 0, TimeSpan.Zero),
                   Incidents = incidents,
                   Kills = 29,
                   FirstOutageAt = new DateTimeOffset(2026, 9, 30, 1, 5, 0, TimeSpan.Zero),
                   LastOutageAt = new DateTimeOffset(2026, 10, 5, 22, 40, 0, TimeSpan.Zero),
                   TimeOfDay = timeOfDay,
                   Backup = backup,
                   Boot = boot,
                   PanelHigh = panel,
                   PanelThresholdBytes = panelThreshold,
                   Permanent = permanent ?? [],
                   Transient = transient ?? [],
                   SnapshotsTruncated = snapshotsTruncated,
                   BootsTruncated = bootsTruncated,
                   ObservedHoursTruncated = runsTruncated,
                   UntrackedProgramEntries = untracked
               };
    }

    /// <summary>
    /// Creates a summary with an unknown time of day, a detected backup, a boot that is not detected, an unknown panel and consumers of both kinds.
    /// </summary>
    /// <param name="generatedAt">The generation time; a fixed one when <c>null</c></param>
    /// <param name="firstLogLineAt">The first log line; a fixed one when <c>null</c></param>
    /// <param name="lookback">The lookback; one hour when <c>null</c></param>
    /// <param name="backup">The backup result; a detected one when <c>null</c></param>
    /// <param name="panelThreshold">The panel threshold in bytes</param>
    /// <param name="permanent">The permanent consumers; one when <c>null</c></param>
    /// <returns>The summary</returns>
    internal static ForensicsSummary Mixed(DateTimeOffset? generatedAt = null, DateTimeOffset? firstLogLineAt = null, TimeSpan? lookback = null, PatternResult? backup = null, ulong panelThreshold = 68 * Mib, IReadOnlyList<ConsumerSummary>? permanent = null)
    {
        return Summary(Undecided(UnknownReason.TooFewIncidents),
                       backup ?? new PatternResult
                                 {
                                     Outcome = PatternOutcome.Detected,
                                     Matching = 3,
                                     Known = 3,
                                     BaseMatching = 3,
                                     BaseKnown = 72
                                 },
                       Decided(),
                       new PatternResult
                       {
                           Outcome = PatternOutcome.Unknown,
                           Reason = UnknownReason.NoPanelMemory
                       },
                       generatedAt,
                       firstLogLineAt,
                       lookback,
                       panelThreshold,
                       permanent ?? [
                                        new ConsumerSummary
                                        {
                                            Program = "mariadbd",
                                            Snapshots = 2016,
                                            Share = 1,
                                            MeanRssBytes = 300 * Mib,
                                            PeakRssBytes = 400 * Mib,
                                            PeakAt = KillAt
                                        }
                                    ],
                       [
                           new ConsumerSummary
                           {
                               Program = "pzstd",
                               Snapshots = 12,
                               Share = 0.006,
                               MeanRssBytes = 100 * Mib,
                               PeakRssBytes = 150 * Mib,
                               PeakAt = KillAt
                           }
                       ]);
    }

    /// <summary>
    /// Creates the activities of an outage.
    /// </summary>
    /// <param name="backup">The backup indicator</param>
    /// <param name="boot">The boot indicator</param>
    /// <param name="panel">The panel indicator</param>
    /// <param name="lastBootAt">The time of the last boot, if any</param>
    /// <returns>The activities</returns>
    internal static IncidentActivities Activities(Indicator backup, Indicator boot, Indicator panel, DateTimeOffset? lastBootAt = null)
    {
        return new IncidentActivities
               {
                   Backup = backup,
                   Boot = boot,
                   LastBootAt = lastBootAt,
                   PanelHigh = panel
               };
    }

    /// <summary>
    /// Creates a kept kill.
    /// </summary>
    /// <param name="command">The victim's command</param>
    /// <returns>The kill</returns>
    internal static IncidentKill Victim(string command)
    {
        return new IncidentKill
               {
                   At = KillAt,
                   Host = "web-1",
                   VictimCommand = command,
                   LineCount = 1
               };
    }

    /// <summary>
    /// Creates an incident of the given kill count whose one kept kill has a victim.
    /// </summary>
    /// <param name="firstKillAt">The time of the first kill</param>
    /// <param name="killCount">The kill count</param>
    /// <param name="victim">The victim's command</param>
    /// <returns>The incident</returns>
    internal static Incident Outage(DateTimeOffset firstKillAt, long killCount, string victim)
    {
        return new Incident
               {
                   Start = firstKillAt.AddHours(-1),
                   End = firstKillAt.AddMinutes(30),
                   FirstKillAt = firstKillAt,
                   LastKillAt = firstKillAt,
                   KillCount = killCount,
                   Kills = [Victim(victim)]
               };
    }

    /// <summary>
    /// Wraps an incident in a stored incident.
    /// </summary>
    /// <param name="id">The ID</param>
    /// <param name="incident">The incident</param>
    /// <returns>The stored incident</returns>
    internal static StoredIncident Stored(long id, Incident incident)
    {
        return new StoredIncident
               {
                   Id = id,
                   Incident = incident,
                   CreatedAt = KillAt,
                   UpdatedAt = KillAt
               };
    }

    #endregion // Methods
}