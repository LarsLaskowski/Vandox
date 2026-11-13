using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds records, policies and passes for the rollup and retention tests of the store.
/// </summary>
internal static class RetentionSamples
{
    #region Constants

    /// <summary>
    /// Undoes schema step 14: the rollup and hold tables, the two indexes and the two meta keys of the retention job.
    /// </summary>
    internal const string UndoStepFourteen = "DROP INDEX records_removable; DROP INDEX gaps_to_at; DROP TABLE metric_rollups_5m; DROP TABLE metric_rollups_1h; DROP TABLE retention_holds; DELETE FROM meta WHERE key IN ('rollup_after_id', 'kill_scan_after_id')";

    /// <summary>
    /// The most steps a test lets a pass take before it fails instead of looping forever.
    /// </summary>
    internal const int MaxSteps = 1000;

    /// <summary>
    /// The source of the metrics the samples write.
    /// </summary>
    internal const string MetricSource = "host";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Gets a period of whole days.
    /// </summary>
    /// <param name="days">The days</param>
    /// <returns>The period</returns>
    internal static TimeSpan Days(double days)
    {
        return TimeSpan.FromDays(days);
    }

    /// <summary>
    /// Creates the policy of the default periods (spec N1) with the default incident windows.
    /// </summary>
    /// <returns>The policy</returns>
    internal static RetentionPolicy Defaults()
    {
        return new RetentionPolicy
               {
                   Raw = Days(30),
                   FiveMinuteRollups = Days(365),
                   HourlyRollups = Days(1095),
                   Logs = Days(90),
                   Snapshots = Days(30),
                   Events = Days(1095),
                   Incidents = null,
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Creates a policy whose finite periods all have the same length; incidents are kept.
    /// </summary>
    /// <param name="period">The period</param>
    /// <returns>The policy</returns>
    internal static RetentionPolicy Uniform(TimeSpan period)
    {
        return new RetentionPolicy
               {
                   Raw = period,
                   FiveMinuteRollups = period,
                   HourlyRollups = period,
                   Logs = period,
                   Snapshots = period,
                   Events = period,
                   Incidents = null,
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Gets the database file of a store directory.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <returns>The path</returns>
    internal static string DatabasePath(string directory)
    {
        return Path.Combine(directory, StorageLimits.FileName);
    }

    /// <summary>
    /// Gets the nanoseconds since the Unix epoch the database stores for an instant, as SQL text.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The number</returns>
    internal static string Ns(DateTimeOffset instant)
    {
        return StorageTime.ToNanoseconds(instant).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Creates a metric record of source <see cref="MetricSource"/>.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="name">The metric name</param>
    /// <param name="value">The value</param>
    /// <param name="labels">The labels, or <c>null</c> for none</param>
    /// <returns>The record</returns>
    internal static DataRecord Metric(string origin, DateTimeOffset capturedAt, string name, double value, Dictionary<string, string>? labels = null)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = MetricSource,
                   CapturedAt = capturedAt,
                   Data = new MetricPoint
                          {
                              Name = name,
                              Value = value,
                              Unit = "percent",
                              Labels = labels
                          }
               };
    }

    /// <summary>
    /// Creates a log line record of source <c>syslog</c>.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="message">The message</param>
    /// <returns>The record</returns>
    internal static DataRecord Line(string origin, DateTimeOffset capturedAt, string message)
    {
        return Samples.LogAt(origin, 0, "syslog", "sshd", capturedAt, message);
    }

    /// <summary>
    /// Creates a record of a kind with a valid payload; <c>kernel_event</c> is a boot, <c>oom_kill</c> a kernel event of an OOM kill.
    /// </summary>
    /// <param name="kind">The record kind, or <c>oom_kill</c></param>
    /// <param name="origin">The origin</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord OfKind(string kind, string origin, DateTimeOffset capturedAt)
    {
        return kind switch
               {
                   RecordKind.Metric => Metric(origin, capturedAt, "cpu", 1),
                   RecordKind.LogLine => Line(origin, capturedAt, "session opened"),
                   RecordKind.ProcessSnapshot => Wrap(origin, "processes", capturedAt, Programs()),
                   RecordKind.ConnectionSnapshot => Wrap(origin, "connections", capturedAt, ListenerSamples.Snapshot("none", "all", ListenerSamples.Tcp("0.0.0.0", 22))),
                   RecordKind.MariaDbStatus => Wrap(origin,
                                                    "mariadb",
                                                    capturedAt,
                                                    new MariaDbStatus
                                                    {
                                                        Availability = MariaDbStatus.Up,
                                                        Complete = true
                                                    }),
                   RecordKind.KernelEvent => Wrap(origin,
                                                  "kernel",
                                                  capturedAt,
                                                  new KernelEvent
                                                  {
                                                      Type = KernelEvent.TypeBoot,
                                                      Boot = new Boot
                                                             {
                                                                 BootId = TimelineSamples.BootId
                                                             }
                                                  }),
                   "oom_kill" => OomKillEvent(origin, capturedAt),
                   RecordKind.ServiceState => Wrap(origin,
                                                   "services",
                                                   capturedAt,
                                                   new ServiceState
                                                   {
                                                       Unit = "nginx.service",
                                                       LoadState = "loaded",
                                                       ActiveState = "active",
                                                       SubState = "running"
                                                   }),
                   RecordKind.BackupRun => Wrap(origin,
                                                "backups",
                                                capturedAt,
                                                new BackupRun
                                                {
                                                    Phase = "start",
                                                    BootId = TimelineSamples.BootId,
                                                    StartedAt = capturedAt,
                                                    Pid = 5150,
                                                    Command = "pzstd",
                                                    Processes = 1
                                                }),
                   _ => Wrap(origin,
                             "backend",
                             capturedAt,
                             new Gap
                             {
                                 From = capturedAt.AddMinutes(-1),
                                 To = capturedAt,
                                 Cause = GapCause.NoData
                             })
               };
    }

    /// <summary>
    /// Creates a kernel event of an OOM kill.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="capturedAt">The capture time</param>
    /// <returns>The record</returns>
    internal static DataRecord OomKillEvent(string origin, DateTimeOffset capturedAt)
    {
        return Wrap(origin,
                    "kernel",
                    capturedAt,
                    new KernelEvent
                    {
                        Type = KernelEvent.TypeOomKill,
                        OomKill = new OomKill
                                  {
                                      VictimPid = 4711,
                                      VictimCommand = "php-fpm"
                                  }
                    });
    }

    /// <summary>
    /// Writes records as one batch received one minute after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <param name="records">The records</param>
    /// <returns>A task that returns the result of the write</returns>
    internal static Task<WriteResult> WriteAsync(SqliteStore store, CancellationToken cancellationToken, params DataRecord[] records)
    {
        return store.WriteBatchAsync(Samples.Batch(records), cancellationToken);
    }

    /// <summary>
    /// Runs fold steps until a step reports that nothing is left.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the steps</param>
    /// <returns>A task that returns the results of the steps</returns>
    internal static async Task<List<RollupStepResult>> FoldAllAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var steps = new List<RollupStepResult>();

        while (steps.Count < MaxSteps)
        {
            var step = await store.FoldRollupsAsync(policy, now, cancellationToken);

            steps.Add(step);

            if (step.More == false)
            {
                return steps;
            }
        }

        Assert.Fail($"the fold did not end within {MaxSteps} steps");

        return steps;
    }

    /// <summary>
    /// Marks every stored record as scanned for kills and stores the given holds, as a kill scan pass would.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="holds">The holds to store</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>A task that completes when the scan is stored</returns>
    internal static async Task MarkScannedAsync(SqliteStore store, IReadOnlyList<RetentionHold> holds, CancellationToken cancellationToken)
    {
        var progress = await store.KillScanProgressAsync(cancellationToken);

        await store.SaveKillScanAsync(holds,
                                      new KillScanProgress
                                      {
                                          ScannedId = progress.HighestId,
                                          HighestId = progress.HighestId
                                      },
                                      cancellationToken);
    }

    /// <summary>
    /// Runs removal steps, carrying the cursor, until a step reports that nothing is left.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the steps</param>
    /// <returns>A task that returns the results of the steps</returns>
    internal static async Task<List<RemovalStepResult>> RemovePassAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var steps = new List<RemovalStepResult>();
        RemovalCursor? cursor = null;

        while (steps.Count < MaxSteps)
        {
            var step = await store.RemoveExpiredAsync(policy, now, cursor, cancellationToken);

            steps.Add(step);

            if (step.More == false)
            {
                return steps;
            }

            cursor = step.Next;
        }

        Assert.Fail($"the removal pass did not end within {MaxSteps} steps");

        return steps;
    }

    /// <summary>
    /// Folds every stored value, marks every record as scanned for kills with the given holds and runs one removal pass.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="policy">The policy</param>
    /// <param name="now">The current time</param>
    /// <param name="holds">The holds to store</param>
    /// <param name="cancellationToken">Cancels the steps</param>
    /// <returns>A task that returns the results of the removal steps</returns>
    internal static async Task<List<RemovalStepResult>> RetainAsync(SqliteStore store, RetentionPolicy policy, DateTimeOffset now, IReadOnlyList<RetentionHold> holds, CancellationToken cancellationToken)
    {
        await FoldAllAsync(store, policy, now, cancellationToken);
        await MarkScannedAsync(store, holds, cancellationToken);

        return await RemovePassAsync(store, policy, now, cancellationToken);
    }

    /// <summary>
    /// Reads the first value of a query on the database of a store directory as a number.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the value</returns>
    internal static Task<long> CountAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        return TimelineSamples.CountAsync(DatabasePath(directory), sql, cancellationToken);
    }

    /// <summary>
    /// Runs statements on the database of a store directory.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The statements</param>
    /// <param name="cancellationToken">Cancels the statements</param>
    /// <returns>A task that completes when the statements ran</returns>
    internal static Task ExecuteAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        return TimelineSamples.ExecuteAsync(DatabasePath(directory), sql, cancellationToken);
    }

    /// <summary>
    /// Creates a hold.
    /// </summary>
    /// <param name="start">The start</param>
    /// <param name="end">The end</param>
    /// <returns>The hold</returns>
    internal static RetentionHold Hold(DateTimeOffset start, DateTimeOffset end)
    {
        return new RetentionHold
               {
                   Start = start,
                   End = end
               };
    }

    /// <summary>
    /// Describes holds as start and end in UTC ticks, earliest first.
    /// </summary>
    /// <param name="holds">The holds</param>
    /// <returns>The descriptions</returns>
    internal static List<string> Describe(IEnumerable<RetentionHold> holds)
    {
        return holds.OrderBy(hold => hold.Start).Select(hold => $"{hold.Start.UtcDateTime:O}-{hold.End.UtcDateTime:O}").ToList();
    }

    /// <summary>
    /// Creates a process snapshot of one program.
    /// </summary>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot Programs()
    {
        return new ProcessSnapshot
               {
                   Complete = true,
                   Programs = [
                                  new ProgramAggregate
                                  {
                                      Program = "nginx",
                                      Count = 1,
                                      RssBytes = 4096
                                  }
                              ]
               };
    }

    /// <summary>
    /// Wraps a payload in a record.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="payload">The payload</param>
    /// <returns>The record</returns>
    private static DataRecord Wrap(string origin, string source, DateTimeOffset capturedAt, IPayload payload)
    {
        return new DataRecord
               {
                   Origin = origin,
                   Source = source,
                   CapturedAt = capturedAt,
                   Data = payload
               };
    }

    #endregion // Methods
}