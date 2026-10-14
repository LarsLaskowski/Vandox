using Vandox.Core.Incidents;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Stored incidents, summaries and notes for the tests of the incident pages and of the note endpoint. Every time of an incident has its own second, so a time that is
/// shown or linked can be told from every other one.
/// </summary>
internal static class IncidentViewSamples
{
    #region Constants

    /// <summary>
    /// The number of the full sample.
    /// </summary>
    internal const long Id = 7;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The start of the window of the full sample.
    /// </summary>
    internal static readonly DateTimeOffset Start = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The time of the first kill.
    /// </summary>
    internal static readonly DateTimeOffset FirstKill = Start.AddMinutes(30);

    /// <summary>
    /// The time of the second kill, which is also the last one.
    /// </summary>
    internal static readonly DateTimeOffset LastKill = FirstKill.AddSeconds(20);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Returns every time of the full sample that the page shows as a link to the logs of that moment.
    /// </summary>
    /// <returns>The times, without repeats</returns>
    internal static IReadOnlyList<DateTimeOffset> LinkedTimes()
    {
        return
        [
            FirstKill,
            LastKill,
            Start.AddMinutes(2).AddSeconds(12),
            Start.AddMinutes(5).AddSeconds(11),
            Start.AddMinutes(10).AddSeconds(21),
            Start.AddMinutes(15).AddSeconds(41),
            Start.AddMinutes(20).AddSeconds(31),
            FirstKill.AddSeconds(1),
            Start.AddMinutes(25).AddSeconds(13),
            Start.AddMinutes(26).AddSeconds(14),
            Start.AddMinutes(27).AddSeconds(15),
            Start.AddMinutes(28).AddSeconds(16),
            Start.AddHours(-1)
        ];
    }

    /// <summary>
    /// Creates the full sample: two kept kills of three, five timeline entries in stored order with the types that get a mark, a hypothesis, two metric extremes, a
    /// connection peak and the templates.
    /// </summary>
    /// <param name="id">The number</param>
    /// <returns>The stored incident</returns>
    internal static StoredIncident Full(long id = Id)
    {
        var incident = new Incident
                       {
                           Start = Start,
                           End = Start.AddHours(2),
                           WindowBeforeSeconds = 1800,
                           WindowAfterSeconds = 5400,
                           FirstKillAt = FirstKill,
                           LastKillAt = LastKill,
                           KillCount = 3,
                           Kills = [
                                       new IncidentKill
                                       {
                                           At = FirstKill,
                                           Host = "web-1",
                                           VictimPid = 4242,
                                           VictimCommand = "mariadbd",
                                           AnonRssBytes = 7400L * 1048576,
                                           OomScoreAdj = 0
                                       },
                                       new IncidentKill
                                       {
                                           At = LastKill,
                                           Host = "web-1",
                                           VictimPid = 4243,
                                           VictimCommand = "php-fpm",
                                           AnonRssBytes = 300L * 1048576,
                                           OomScoreAdj = 200
                                       }
                                   ],
                           Timeline = [
                                          Entry(Start.AddMinutes(5).AddSeconds(11), "boot", "kernel", "Linux 5.15"),
                                          Entry(Start.AddMinutes(10).AddSeconds(21), "backup_first_seen", "program", "pzstd"),
                                          Entry(Start.AddMinutes(15).AddSeconds(41), "service_start", "unit", "mariadb.service"),
                                          Entry(Start.AddMinutes(20).AddSeconds(31), "backup_last_seen", "program", "pzstd"),
                                          Entry(FirstKill.AddSeconds(1), "oom_kill", "victim", "mariadbd")
                                      ],
                           TimelineOmitted = 37,
                           Hypothesis = new IncidentHypothesis
                                        {
                                            SnapshotCount = 3,
                                            GrowthKnown = true,
                                            Growth = [
                                                         new ProgramGrowth
                                                         {
                                                             Program = "mariadbd",
                                                             StartRssBytes = 100L * 1048576,
                                                             EndRssBytes = 868L * 1048576,
                                                             GrowthBytes = 768UL * 1048576
                                                         }
                                                     ],
                                            Consumers = [
                                                            new ProgramUsage
                                                            {
                                                                Program = "apache2",
                                                                RssBytes = 400UL * 1048576,
                                                                Count = 4
                                                            }
                                                        ],
                                            BackupRunning = true,
                                            BackupPrograms = ["pzstd"],
                                            RecentBootAt = Start.AddMinutes(2).AddSeconds(12),
                                            ServiceEvents = 2
                                        },
                           Metrics = [
                                         new MetricExtreme
                                         {
                                             Name = "mem_available_bytes",
                                             Extreme = "min",
                                             Source = "proc.meminfo",
                                             At = Start.AddMinutes(25).AddSeconds(13),
                                             Value = 300000000,
                                             Samples = 9
                                         },
                                         new MetricExtreme
                                         {
                                             Name = "swap_used_bytes",
                                             Extreme = "max",
                                             Source = "proc.meminfo",
                                             At = Start.AddMinutes(26).AddSeconds(14),
                                             Value = 900000000,
                                             Samples = 9
                                         }
                                     ],
                           Connections = new ConnectionPeak
                                         {
                                             Snapshots = 9,
                                             At = Start.AddMinutes(27).AddSeconds(15),
                                             RecordId = 11,
                                             Sockets = 321,
                                             Processes = [
                                                             new ConnectionProcess
                                                             {
                                                                 Command = "apache2",
                                                                 Count = 200
                                                             },
                                                             new ConnectionProcess
                                                             {
                                                                 Command = "mariadbd",
                                                                 Count = 100
                                                             },
                                                             new ConnectionProcess
                                                             {
                                                                 Command = "sshd",
                                                                 Count = 21
                                                             }
                                                         ]
                                         },
                           NewTemplates = [
                                              new IncidentNewTemplate
                                              {
                                                  Source = "syslog",
                                                  Program = "cron",
                                                  Template = "job <num> failed",
                                                  FirstSeen = Start.AddMinutes(28).AddSeconds(16),
                                                  Count = 5
                                              }
                                          ],
                           TemplateSpikes = [
                                                new IncidentTemplateSpike
                                                {
                                                    Source = "syslog",
                                                    Program = "cron",
                                                    Template = "job <num> failed",
                                                    Hour = Start.AddHours(-1),
                                                    Count = 40,
                                                    UsualLevel = 1.5
                                                }
                                            ]
                       };

        return new StoredIncident
               {
                   Id = id,
                   Incident = incident,
                   CreatedAt = Start.AddDays(1),
                   UpdatedAt = Start.AddDays(1).AddMinutes(5)
               };
    }

    /// <summary>
    /// Creates a summary that starts as many hours after the start of the full sample as its number says, so a higher number is newer.
    /// </summary>
    /// <param name="id">The number</param>
    /// <param name="victim">The command of the first victim</param>
    /// <returns>The summary</returns>
    internal static IncidentSummary Summary(long id, string victim = "mariadbd")
    {
        var start = Start.AddHours(id);

        return new IncidentSummary
               {
                   Id = id,
                   Start = start,
                   End = start.AddHours(2),
                   FirstKillAt = start.AddMinutes(30),
                   LastKillAt = start.AddMinutes(30).AddSeconds(125),
                   KillCount = 2,
                   FirstVictim = victim,
                   Hypothesis = new IncidentHypothesis
                                {
                                    GrowthKnown = true,
                                    Growth = [
                                                 new ProgramGrowth
                                                 {
                                                     Program = "pzstd",
                                                     StartRssBytes = 1,
                                                     EndRssBytes = 2,
                                                     GrowthBytes = 512UL * 1048576
                                                 }
                                             ],
                                    BackupRunning = false
                                },
                   CreatedAt = start.AddDays(1),
                   UpdatedAt = start.AddDays(1)
               };
    }

    /// <summary>
    /// Creates a note.
    /// </summary>
    /// <param name="incidentId">The incident it belongs to now</param>
    /// <param name="writtenFor">The number it was written for</param>
    /// <param name="text">The text</param>
    /// <returns>The note</returns>
    internal static IncidentNote Note(long incidentId, long writtenFor, string text)
    {
        return new IncidentNote
               {
                   IncidentId = incidentId,
                   WrittenFor = writtenFor,
                   Text = text,
                   UpdatedAt = Start.AddDays(2).AddSeconds(writtenFor)
               };
    }

    /// <summary>
    /// Creates a timeline entry with one text.
    /// </summary>
    /// <param name="at">The time</param>
    /// <param name="type">The type</param>
    /// <param name="key">The key of the text</param>
    /// <param name="value">The value of the text</param>
    /// <returns>The entry</returns>
    internal static TimelineEntry Entry(DateTimeOffset at, string type, string key, string value)
    {
        return new TimelineEntry
               {
                   At = at,
                   Type = type,
                   Host = "web-1",
                   Texts = new Dictionary<string, string>
                           {
                               [key] = value
                           }
               };
    }

    #endregion // Methods
}