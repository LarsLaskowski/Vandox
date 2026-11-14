using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Retention;
using Vandox.Core.Configuration;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="RetentionService"/>
/// </summary>
[TestClass]
public class RetentionServiceTests
{
    #region Constants

    private const string RollupLine = "retention rollup";

    private const string RecordsLine = "retention removal records";

    private const string DerivedLine = "retention removal derived";

    private const string BusyLine = "retention step busy";

    private const string FailedLine = "retention step failed";

    private const string Sentinel = "SENTINEL-STORED-TEXT";

    private static readonly DateTimeOffset _start = new(2026, 11, 13, 10, 2, 30, TimeSpan.Zero);

    private static readonly TimeSpan _fiveMinutes = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _guard = TimeSpan.FromSeconds(20);

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The policy takes each of the seven <c>retention.*</c> keys as its period, <c>unlimited</c> as no period, and the kill window from <c>incidents.window_before</c> and <c>incidents.window_after</c>.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void RetentionServiceCreatePolicyReadsEveryKey()
    {
        // Arrange
        var distinct = new BackendConfig();
        var unlimited = new BackendConfig();
        var defaults = new BackendConfig();

        distinct.Retention.Raw = "1d";
        distinct.Retention.FiveMinuteRollups = "2d";
        distinct.Retention.HourlyRollups = "3d";
        distinct.Retention.Logs = "4d";
        distinct.Retention.Snapshots = "5d";
        distinct.Retention.Events = "6d";
        distinct.Retention.Incidents = "9999d";
        distinct.Incidents.WindowBefore = "2h";
        distinct.Incidents.WindowAfter = "45m";
        unlimited.Retention.Raw = RetentionPeriod.Unlimited;
        unlimited.Retention.FiveMinuteRollups = RetentionPeriod.Unlimited;
        unlimited.Retention.HourlyRollups = RetentionPeriod.Unlimited;
        unlimited.Retention.Logs = RetentionPeriod.Unlimited;
        unlimited.Retention.Snapshots = RetentionPeriod.Unlimited;
        unlimited.Retention.Events = RetentionPeriod.Unlimited;
        unlimited.Retention.Incidents = RetentionPeriod.Unlimited;

        // Act
        var fromDistinct = RetentionService.CreatePolicy(distinct);
        var fromUnlimited = RetentionService.CreatePolicy(unlimited);
        var fromDefaults = RetentionService.CreatePolicy(defaults);

        // Assert
        Assert.AreSequenceEqual(Days(1, 2, 3, 4, 5, 6, 9999), Periods(fromDistinct), "each key gives its own period, in the order raw, rollups_5m, rollups_1h, logs, snapshots, events, incidents");
        Assert.AreEqual(TimeSpan.FromHours(2), fromDistinct.KillWindowBefore, "the kill window before from incidents.window_before");
        Assert.AreEqual(TimeSpan.FromMinutes(45), fromDistinct.KillWindowAfter, "the kill window after from incidents.window_after");
        Assert.IsTrue(Periods(fromUnlimited).All(period => period is null), "unlimited is no period for every key");
        Assert.AreSequenceEqual(Days(30, 365, 1095, 90, 30, 1095, null), Periods(fromDefaults), "the default periods");
        Assert.AreEqual(TimeSpan.FromHours(1), fromDefaults.KillWindowBefore, "the default kill window before");
        Assert.AreEqual(TimeSpan.FromMinutes(30), fromDefaults.KillWindowAfter, "the default kill window after");
    }

    /// <summary>
    /// The running service folds at start and then on every 5-minute tick of its clock, so a bucket is folded at most 5 minutes after it ends; it scans the kills and removes at start, after the fold, and again once an hour has passed.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task RetentionServiceFoldsWithinFiveMinutesOfABucketEnd()
    {
        // Arrange
        const int ticks = 13;

        var store = new ScriptedRetentionStore();
        var clock = new TimerSignalingClock(_start);
        var policy = Policy();
        using var service = new RetentionService(store, policy, clock, new ListLogger());

        // Act
        await service.StartAsync(TestContext.CancellationToken);
        await WaitForAsync(service, store.WaitForCallsAsync(ScriptedRetentionStore.Remove, 1, TestContext.CancellationToken));

        var startCalls = store.Calls.ToList();

        for (var tick = 1; tick <= ticks; tick++)
        {
            await WaitForAsync(service, clock.WaitForTimersAsync(_fiveMinutes, 1, TestContext.CancellationToken));
            clock.Advance(_fiveMinutes);
            await WaitForAsync(service, store.WaitForCallsAsync(ScriptedRetentionStore.Fold, tick + 1, TestContext.CancellationToken));
        }

        await WaitForAsync(service, store.WaitForCallsAsync(ScriptedRetentionStore.Remove, 2, TestContext.CancellationToken));
        await service.StopAsync(TestContext.CancellationToken);

        var removals = store.RemovalTimes;

        // Assert
        Assert.AreSequenceEqual(Enumerable.Range(0, ticks + 1).Select(tick => _start + (tick * _fiveMinutes)).ToList(), store.FoldTimes.ToList(), "one fold at start and one on every 5-minute tick, each with the clock's time");
        Assert.IsLessThan(startCalls.IndexOf(ScriptedRetentionStore.Progress), startCalls.IndexOf(ScriptedRetentionStore.Fold), "the start pass folds before the kill scan");
        Assert.IsLessThan(startCalls.IndexOf(ScriptedRetentionStore.Remove), startCalls.IndexOf(ScriptedRetentionStore.Progress), "the kill scan comes before the removal");
        Assert.HasCount(2, removals, "a removal pass at start and one after an hour, none on the ticks in between");
        Assert.AreEqual(_start, removals[0], "the first removal pass runs at start");
        Assert.IsTrue(removals[1] == _start.AddMinutes(60) || removals[1] == _start.AddMinutes(65), $"the second removal pass runs on the first tick an hour after the first, not at {removals[1]:O}");
        Assert.IsTrue(store.Policies.All(item => ReferenceEquals(item, policy)), "every step gets the service's policy");
    }

    /// <summary>
    /// A fold pass logs the values and buckets of all its steps in one line, and a removal pass logs the sums of all its steps in a line of the records and a line of the derived data, each count in its own field; every step continues at the cursor of the one before.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC10")]
    public async Task RetentionServiceLogsRollupAndRemovalCounts()
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var capture = new IngestLogCapture();
        var events = new ListLogger();
        var cursor = new RemovalCursor
                     {
                         Kind = RecordKind.LogLine,
                         CapturedAt = _start.AddDays(-100),
                         Id = 42
                     };

        store.AddFold(3, 2, true);
        store.AddFold(4, 1, false);
        store.AddRemoval(new RemovalStepResult
                         {
                             MetricValues = 1,
                             LogLines = 2,
                             Snapshots = 3,
                             EventRecords = 4,
                             Gaps = 5,
                             FiveMinuteValues = 6,
                             HourlyValues = 7,
                             TemplateHours = 8,
                             RemovedIncidents = 9,
                             Holds = 10,
                             More = true,
                             Next = cursor
                         });
        store.AddRemoval(new RemovalStepResult
                         {
                             MetricValues = 10,
                             LogLines = 20,
                             Snapshots = 30,
                             EventRecords = 40,
                             Gaps = 50,
                             FiveMinuteValues = 60,
                             HourlyValues = 70,
                             TemplateHours = 80,
                             RemovedIncidents = 90,
                             Holds = 100,
                             More = false
                         });

        using var factory = CreateFactory(capture, events);
        using var service = new RetentionService(store, Policy(), new FakeTimeProvider(_start), factory.CreateLogger("vandoxd"));

        // Act
        await service.RollUpAsync(TestContext.CancellationToken);
        await service.RemoveAsync(TestContext.CancellationToken);

        var entries = capture.Entries;

        // Assert
        Assert.AreSequenceEqual([RollupLine, RecordsLine, DerivedLine], entries.Select(entry => entry.Name).ToList(), "the three lines in order");
        Assert.AreSequenceEqual([(LogLevel.Information, new EventId(69)), (LogLevel.Information, new EventId(70)), (LogLevel.Information, new EventId(73))], events.Entries, "levels and event IDs");
        Assert.AreSequenceEqual(["retention rollup 7 3", "retention removal records 11 22 33 44 55", "retention removal derived 66 77 88 99 110"], entries.Select(entry => entry.Text).ToList(), "the texts with the summed counts");
        AssertValues(entries[0], ("Values", "7"), ("Buckets", "3"));
        AssertValues(entries[1], ("MetricValues", "11"), ("LogLines", "22"), ("Snapshots", "33"), ("Events", "44"), ("Gaps", "55"));
        AssertValues(entries[2], ("FiveMinuteValues", "66"), ("HourlyValues", "77"), ("TemplateHours", "88"), ("Incidents", "99"), ("Holds", "110"));
        Assert.HasCount(2, store.FoldTimes, "the fold pass ends with the step that reports no more");
        Assert.HasCount(2, store.Cursors, "the removal pass ends with the step that reports no more");
        Assert.IsNull(store.Cursors[0], "the pass starts without a cursor");
        Assert.AreSame(cursor, store.Cursors[1], "the second step continues at the cursor of the first");
    }

    /// <summary>
    /// A removal pass that removed records but no derived data writes only the line of the records.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC10")]
    public async Task RetentionServiceLogsOnlyRecordsLineWhenNoDerivedDataRemoved()
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var capture = new IngestLogCapture();
        var events = new ListLogger();

        store.AddRemoval(new RemovalStepResult
                         {
                             LogLines = 4,
                             More = false
                         });

        using var factory = CreateFactory(capture, events);
        using var service = new RetentionService(store, Policy(), new FakeTimeProvider(_start), factory.CreateLogger("vandoxd"));

        // Act
        await service.RemoveAsync(TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["retention removal records 0 4 0 0 0"], capture.Entries.Select(entry => entry.Text).ToList(), "only the line of the records");
        Assert.AreSequenceEqual([(LogLevel.Information, new EventId(70))], events.Entries, "its level and event ID");
        Assert.HasCount(1, store.Cursors, "the pass ran its step");
    }

    /// <summary>
    /// A fold pass that folded nothing and a removal pass that removed nothing write no line.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC10")]
    public async Task RetentionServiceLogsNothingForAPassWithoutChange()
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var events = new ListLogger();

        store.AddFold(0, 0, false);
        store.AddRemoval(new RemovalStepResult());

        using var service = new RetentionService(store, Policy(), new FakeTimeProvider(_start), events);

        // Act
        await service.RollUpAsync(TestContext.CancellationToken);
        await service.RemoveAsync(TestContext.CancellationToken);

        // Assert
        Assert.IsEmpty(events.Messages, "no line for passes that changed nothing");
        Assert.HasCount(1, store.FoldTimes, "the fold pass ran");
        Assert.HasCount(1, store.Cursors, "the removal pass ran");
    }

    /// <summary>
    /// A step that fails logs one error with its operation and the store's error, never stored text, and ends the pass.
    /// </summary>
    /// <param name="operation">The operation that fails: fold, scan or remove</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC10")]
    [DataRow("fold")]
    [DataRow("scan")]
    [DataRow("remove")]
    public async Task RetentionServiceLogsFailedStepWithOperation(string operation)
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var capture = new IngestLogCapture();
        var events = new ListLogger();
        var error = ArrangeFailure(store, operation, ScriptedRetentionStore.Failed);

        using var factory = CreateFactory(capture, events);
        using var service = new RetentionService(store, Policy(), new FakeTimeProvider(_start), factory.CreateLogger("vandoxd"));

        // Act
        await RunPassAsync(service, operation);

        var entries = capture.Entries;

        // Assert
        Assert.AreSequenceEqual([$"{FailedLine} {operation} {error}"], entries.Select(entry => entry.Text).ToList(), "one line with the operation and the store's error");
        Assert.AreSequenceEqual([(LogLevel.Error, new EventId(72))], events.Entries, "an error with its event ID");
        AssertValues(entries[0], ("Operation", operation), ("Error", error));
        Assert.DoesNotContain(text => text.Contains(Sentinel, StringComparison.Ordinal), capture.AllTexts(), "no stored text is logged");
        Assert.AreEqual(StepsAfterFailure(operation), StepCalls(store, operation), "the pass ends at the failed step");
    }

    /// <summary>
    /// A step that finds the database busy logs a warning with its operation and no error, and the next pass does the work.
    /// </summary>
    /// <param name="operation">The operation that finds the database busy: fold, scan or remove</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow("fold")]
    [DataRow("scan")]
    [DataRow("remove")]
    public async Task RetentionServiceLogsBusyAndContinuesNextPass(string operation)
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var capture = new IngestLogCapture();
        var events = new ListLogger();

        ArrangeFailure(store, operation, ScriptedRetentionStore.Busy);
        store.AddFold(5, 2, false);
        store.AddRemoval(new RemovalStepResult
                         {
                             LogLines = 3,
                             More = false
                         });

        using var factory = CreateFactory(capture, events);
        using var service = new RetentionService(store, Policy(), new FakeTimeProvider(_start), factory.CreateLogger("vandoxd"));

        // Act
        await RunPassAsync(service, operation);
        await RunPassAsync(service, operation);

        var entries = capture.Entries;
        var done = operation == "fold" ? "retention rollup 5 2" : "retention removal records 0 3 0 0 0";
        var doneEvent = operation == "fold" ? 69 : 70;

        // Assert
        Assert.AreSequenceEqual([$"{BusyLine} {operation}", done], entries.Select(entry => entry.Text).ToList(), "a busy warning, then the work of the next pass");
        Assert.AreSequenceEqual([(LogLevel.Warning, new EventId(71)), (LogLevel.Information, new EventId(doneEvent))], events.Entries, "a warning, no error");
        AssertValues(entries[0], ("Operation", operation));
    }

    /// <summary>
    /// Stopping the service while a step runs waits for that step and starts no further step, although the step reported more work.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task RetentionServiceStopsBetweenSteps()
    {
        // Arrange
        var store = new ScriptedRetentionStore();
        var gate = new TaskCompletionSource<RollupStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new RetentionService(store, Policy(), new TimerSignalingClock(_start), new ListLogger());

        store.AddFold(() => gate.Task);
        store.AddFold(1, 1, false);

        await service.StartAsync(TestContext.CancellationToken);
        await WaitForAsync(service, store.WaitForCallsAsync(ScriptedRetentionStore.Fold, 1, TestContext.CancellationToken));

        // Act
        var stopping = service.StopAsync(TestContext.CancellationToken);
        var stoppedDuringStep = stopping.IsCompleted;

        gate.SetResult(new RollupStepResult
                       {
                           Values = 10,
                           Buckets = 2,
                           More = true
                       });
        await stopping.WaitAsync(_guard, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(stoppedDuringStep, "the stop waits for the running step");
        Assert.HasCount(1, store.FoldTimes, "no fold step after the stop, although the step reported more");
        Assert.IsEmpty(store.Cursors, "no removal step after the stop");
    }

    /// <summary>
    /// Returns the periods of a policy in the order of the keys.
    /// </summary>
    /// <param name="policy">The policy</param>
    /// <returns>raw, rollups_5m, rollups_1h, logs, snapshots, events, incidents</returns>
    private static List<TimeSpan?> Periods(RetentionPolicy policy)
    {
        return [policy.Raw, policy.FiveMinuteRollups, policy.HourlyRollups, policy.Logs, policy.Snapshots, policy.Events, policy.Incidents];
    }

    /// <summary>
    /// Returns periods of whole days.
    /// </summary>
    /// <param name="days">The days, <c>null</c> for no period</param>
    /// <returns>The periods</returns>
    private static List<TimeSpan?> Days(params int?[] days)
    {
        return days.Select(day => day is { } value ? TimeSpan.FromDays(value) : (TimeSpan?)null).ToList();
    }

    /// <summary>
    /// Creates the default policy.
    /// </summary>
    /// <returns>The policy</returns>
    private static RetentionPolicy Policy()
    {
        return new RetentionPolicy
               {
                   Raw = TimeSpan.FromDays(30),
                   FiveMinuteRollups = TimeSpan.FromDays(365),
                   HourlyRollups = TimeSpan.FromDays(1095),
                   Logs = TimeSpan.FromDays(90),
                   Snapshots = TimeSpan.FromDays(30),
                   Events = TimeSpan.FromDays(1095),
                   KillWindowBefore = TimeSpan.FromHours(1),
                   KillWindowAfter = TimeSpan.FromMinutes(30)
               };
    }

    /// <summary>
    /// Creates a logger factory that writes to both captures.
    /// </summary>
    /// <param name="capture">Captures the names and fields</param>
    /// <param name="events">Captures the levels and event IDs</param>
    /// <returns>The factory</returns>
    private static ILoggerFactory CreateFactory(IngestLogCapture capture, ListLogger events)
    {
        return LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(capture).AddProvider(new ListLoggerProvider(events)));
    }

    /// <summary>
    /// Asserts the fields of a log entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="values">The field names and their values</param>
    private static void AssertValues(IngestLogEntry entry, params (string Name, string Value)[] values)
    {
        Assert.AreSequenceEqual(values.Select(value => value.Name).Order(StringComparer.Ordinal).ToList(), entry.Values.Keys.Order(StringComparer.Ordinal).ToList(), $"the fields of {entry.Name}");

        foreach (var (name, value) in values)
        {
            Assert.AreEqual(value, entry.Value(name), $"field {name} of {entry.Name}");
        }
    }

    /// <summary>
    /// Makes the first step of an operation throw, with a step queued behind it that must not run in the same pass; for the scan, a kill whose victim carries stored text.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="operation">fold, scan or remove</param>
    /// <param name="exception">Creates the exception from the store's operation text</param>
    /// <returns>The message of the exception</returns>
    private static string ArrangeFailure(ScriptedRetentionStore store, string operation, Func<string, StoreException> exception)
    {
        StoreException thrown;

        switch (operation)
        {
            case "fold":
                {
                    thrown = exception("store: folding rollups");
                    store.AddFold(thrown);
                }
                break;

            case "scan":
                {
                    thrown = exception("store: retention");
                    store.KernelEvents.Add(SentinelKill(7));
                    store.ScanProgress = new KillScanProgress
                                         {
                                             ScannedId = 0,
                                             HighestId = 7
                                         };
                    store.FailNextSave(thrown);
                }
                break;

            default:
                {
                    thrown = exception("store: removing expired data");
                    store.AddRemoval(thrown);
                }
                break;
        }

        return thrown.Message;
    }

    /// <summary>
    /// Creates an agent OOM kill whose victim and message carry the sentinel.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <returns>The stored record</returns>
    private static StoredRecord SentinelKill(long id)
    {
        return new StoredRecord
               {
                   Id = id,
                   AgentId = "web-1",
                   ReceivedAt = _start,
                   Record = new DataRecord
                            {
                                Origin = RecordOrigin.Agent,
                                Source = "kernel",
                                Seq = (ulong)id,
                                CapturedAt = _start.AddDays(-1),
                                Data = new KernelEvent
                                       {
                                           Type = KernelEvent.TypeOomKill,
                                           Message = Sentinel,
                                           OomKill = new OomKill
                                                     {
                                                         VictimPid = 4711,
                                                         VictimCommand = Sentinel
                                                     }
                                       }
                            }
               };
    }

    /// <summary>
    /// Returns the number of calls of the failed operation's steps expected after the failure: the failed step only, and no removal after a failed scan.
    /// </summary>
    /// <param name="operation">fold, scan or remove</param>
    /// <returns>The number of calls</returns>
    private static int StepsAfterFailure(string operation)
    {
        return operation == "scan" ? 0 : 1;
    }

    /// <summary>
    /// Counts the step calls that follow a failure of the operation: fold steps for the fold, removal steps for the scan and the removal.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="operation">fold, scan or remove</param>
    /// <returns>The number of calls</returns>
    private static int StepCalls(ScriptedRetentionStore store, string operation)
    {
        return operation == "fold" ? store.FoldTimes.Count : store.Cursors.Count;
    }

    /// <summary>
    /// Runs the pass of an operation: the fold pass for the fold, the removal pass with its kill scan otherwise.
    /// </summary>
    /// <param name="service">The service</param>
    /// <param name="operation">fold, scan or remove</param>
    /// <returns>A task that completes when the pass ends</returns>
    private Task RunPassAsync(RetentionService service, string operation)
    {
        return operation == "fold" ? service.RollUpAsync(TestContext.CancellationToken) : service.RemoveAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// Waits for a signal of the store or the clock; fails when the service's work ends first.
    /// </summary>
    /// <param name="service">The started service</param>
    /// <param name="signal">The signal</param>
    /// <returns>A task that completes when the signal arrived</returns>
    private async Task WaitForAsync(RetentionService service, Task signal)
    {
        var work = service.ExecuteTask ?? Task.CompletedTask;
        var first = await Task.WhenAny(signal, work).WaitAsync(_guard, TestContext.CancellationToken);

        if (first != signal)
        {
            Assert.Fail($"the service ended before the signal: {work.Exception?.InnerException?.Message ?? "no error"}");
        }

        await signal;
    }

    #endregion // Methods
}