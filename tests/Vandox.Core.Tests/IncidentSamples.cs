using Vandox.Core.Incidents;
using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds detected events, clusters, snapshots and metrics for the incident tests.
/// </summary>
internal static class IncidentSamples
{
    #region Constants

    /// <summary>
    /// The window before the first kill that the incident tests use.
    /// </summary>
    internal static readonly TimeSpan Before = TimeSpan.FromHours(1);

    /// <summary>
    /// The window after the last kill that the incident tests use.
    /// </summary>
    internal static readonly TimeSpan After = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The time of the first kill of the incident tests.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a detected event without fields.
    /// </summary>
    /// <param name="signatureId">The signature ID</param>
    /// <param name="at">The time of the first line</param>
    /// <param name="recordId">The ID of the only record</param>
    /// <returns>The event</returns>
    internal static DetectedEvent Event(string signatureId, DateTimeOffset at, long recordId = 1)
    {
        return new DetectedEvent
               {
                   SignatureId = signatureId,
                   CapturedAt = at,
                   Host = "web-1",
                   RecordIds = [recordId],
                   LineCount = 1
               };
    }

    /// <summary>
    /// Creates a detected OOM kill with the fields of the built-in signature.
    /// </summary>
    /// <param name="at">The time of the kill</param>
    /// <param name="pid">The victim's process ID</param>
    /// <param name="command">The victim's command</param>
    /// <param name="recordId">The ID of the only record</param>
    /// <returns>The event</returns>
    internal static DetectedEvent Kill(DateTimeOffset at, long pid = 4242, string command = "mariadbd", long recordId = 1)
    {
        var detected = Event(IncidentSignatures.OomKill, at, recordId);

        detected.Texts = new Dictionary<string, string>
                         {
                             ["victim_command"] = command
                         };
        detected.Numbers = new Dictionary<string, long>
                           {
                               ["victim_pid"] = pid,
                               ["anon_rss_bytes"] = 7577600000,
                               ["oom_score_adj"] = 0
                           };

        return detected;
    }

    /// <summary>
    /// Creates a detected service start with a unit and a description.
    /// </summary>
    /// <param name="at">The time of the line</param>
    /// <param name="recordId">The ID of the only record</param>
    /// <returns>The event</returns>
    internal static DetectedEvent ServiceStart(DateTimeOffset at, long recordId = 1)
    {
        var detected = Event(IncidentSignatures.ServiceStart, at, recordId);

        detected.Texts = new Dictionary<string, string>
                         {
                             ["unit"] = "mariadb.service",
                             ["description"] = "MariaDB database server"
                         };

        return detected;
    }

    /// <summary>
    /// Creates the cluster of one kill with the window of the incident tests.
    /// </summary>
    /// <param name="firstKillAt">The time of the kill</param>
    /// <returns>The cluster</returns>
    internal static KillCluster Cluster(DateTimeOffset firstKillAt)
    {
        return new KillCluster
               {
                   Start = firstKillAt - Before,
                   End = firstKillAt + After,
                   FirstKillAt = firstKillAt,
                   LastKillAt = firstKillAt,
                   KillCount = 1,
                   Kills = [
                               new IncidentKill
                               {
                                   At = firstKillAt,
                                   Host = "web-1",
                                   VictimPid = 4242,
                                   VictimCommand = "mariadbd",
                                   RecordIds = [1],
                                   LineCount = 1
                               }
                           ]
               };
    }

    /// <summary>
    /// Creates a builder for the cluster of one kill with the window of the incident tests.
    /// </summary>
    /// <param name="firstKillAt">The time of the kill</param>
    /// <returns>The builder</returns>
    internal static IncidentBuilder Builder(DateTimeOffset firstKillAt)
    {
        return new IncidentBuilder(Cluster(firstKillAt), Before, After);
    }

    /// <summary>
    /// Creates a process snapshot that holds program aggregates, one process per program.
    /// </summary>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The snapshot</returns>
    internal static ProcessSnapshot Programs(params (string Program, ulong Rss)[] programs)
    {
        return new ProcessSnapshot
               {
                   Complete = true,
                   Programs = programs.Select(entry => new ProgramAggregate
                                                       {
                                                           Program = entry.Program,
                                                           Count = 1,
                                                           RssBytes = entry.Rss
                                                       })
                                      .ToList()
               };
    }

    /// <summary>
    /// Creates a process snapshot that holds processes only.
    /// </summary>
    /// <param name="processes">The commands with their resident memory in bytes</param>
    /// <returns>The snapshot</returns>
    internal static ProcessSnapshot Processes(params (string Command, ulong Rss)[] processes)
    {
        return new ProcessSnapshot
               {
                   Complete = true,
                   Processes = processes.Select((entry, index) => new ProcessSample
                                                                  {
                                                                      Pid = index + 1,
                                                                      Command = entry.Command,
                                                                      RssBytes = entry.Rss
                                                                  })
                                        .ToList()
               };
    }

    /// <summary>
    /// Creates a connection snapshot with state counts and per-process counts.
    /// </summary>
    /// <param name="established">The number of established TCP sockets</param>
    /// <param name="processes">The commands with their socket counts</param>
    /// <returns>The snapshot</returns>
    internal static ConnectionSnapshot Connections(uint established, params (string Command, uint Count)[] processes)
    {
        return new ConnectionSnapshot
               {
                   Complete = true,
                   States = [
                                new StateCount
                                {
                                    Proto = Proto.Tcp,
                                    State = "ESTABLISHED",
                                    Count = established
                                }
                            ],
                   Processes = processes.Select((entry, index) => new ProcessConnections
                                                                  {
                                                                      Pid = index + 1,
                                                                      Command = entry.Command,
                                                                      Count = entry.Count
                                                                  })
                                        .ToList()
               };
    }

    /// <summary>
    /// Creates a metric point.
    /// </summary>
    /// <param name="name">The metric name</param>
    /// <param name="value">The value</param>
    /// <param name="labels">The labels, none for an unlabeled metric</param>
    /// <returns>The metric</returns>
    internal static MetricPoint Metric(string name, double value, params (string Key, string Value)[] labels)
    {
        return new MetricPoint
               {
                   Name = name,
                   Value = value,
                   Labels = labels.Length == 0 ? null : labels.ToDictionary(label => label.Key, label => label.Value)
               };
    }

    #endregion // Methods
}