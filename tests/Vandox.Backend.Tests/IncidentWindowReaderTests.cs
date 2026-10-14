using Vandox.Backend.Incidents;
using Vandox.Core.Incidents;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IncidentWindowReader"/>
/// </summary>
[TestClass]
public class IncidentWindowReaderTests
{
    #region Constants

    private const ulong Mib = 1048576UL;

    private const string Legacy = "legacy-monitor";

    private const string MemInfo = "proc.meminfo";

    private const string Net = "proc.net";

    #endregion // Constants

    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The memory chart holds the process snapshots of the window, from its start up to, but not including, its end, of the source of the earliest of them.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderChartsMemoryPerProgramWithinTheWindow()
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var incident = Window(TimeSpan.FromHours(1));

        AddSeven(records, "before-source", _start.AddMinutes(-1), 1);
        AddSeven(records, Legacy, _start, 1);
        AddSeven(records, "other-monitor", _start.AddMinutes(5), 50);
        AddSeven(records, Legacy, _start.AddMinutes(10), 2);
        AddSeven(records, Legacy, _start.AddMinutes(20), 3);
        AddSeven(records, Legacy, incident.End, 4);
        AddSeven(records, Legacy, incident.End.AddMinutes(5), 5);

        // Act
        var data = await new IncidentWindowReader(records).ReadAsync(incident, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(data.Memory, "the memory chart");
        Assert.AreEqual(Legacy, data.MemorySource, "the source of the earliest snapshot in the window");
        Assert.IsTrue(data.Memory.Stacked, "the chart is stacked");
        Assert.AreSequenceEqual(new[] { _start, _start.AddMinutes(10), _start.AddMinutes(20) }, data.Memory.Times.ToArray(), "the snapshots of the window, the one at its start and not the one at its end");
        Assert.AreSequenceEqual(new[] { "p7", "p6", "p5", "p4", "p3", IncidentsView.OtherPrograms }, data.Memory.Series.Select(series => series.Label).ToArray(), "the five largest programs and the others");
        Assert.AreSequenceEqual(new double?[] { 70, 140, 210 }, data.Memory.Series[0].Values.ToArray(), "the largest program in MiB, per snapshot");
        Assert.AreSequenceEqual(new double?[] { 30, 60, 90 }, data.Memory.Series[5].Values.ToArray(), "the two smallest programs together in MiB");
        Assert.IsNull(data.MemoryCutAt, "nothing was cut");
    }

    /// <summary>
    /// With two sources in the window and more than one page of each, every page query carries the source of the earliest snapshot, so the chart holds that source only.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderUsesTheSourceOfTheFirstSnapshot()
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var incident = Window(TimeSpan.FromHours(1));

        for (var index = 0; index < 20; index++)
        {
            AddSeven(records, "agent-a", _start.AddSeconds(2 * index), 1, "pa");
            AddSeven(records, "agent-b", _start.AddSeconds((2 * index) + 1), 1, "pb");
        }

        // Act
        var data = await new IncidentWindowReader(records).ReadAsync(incident, TestContext.CancellationToken);

        // Assert
        var processQueries = records.Queries.Where(query => query.Kind == RecordKind.ProcessSnapshot).ToArray();

        Assert.IsNotNull(data.Memory, "the memory chart");
        Assert.AreEqual("agent-a", data.MemorySource, "the source of the earliest snapshot");
        Assert.HasCount(20, data.Memory.Times, "the snapshots of that source");
        Assert.IsTrue(data.Memory.Times.All(time => (time - _start).TotalSeconds % 2 == 0), "no snapshot of the other source");
        Assert.DoesNotContain(series => series.Label.StartsWith("pb", StringComparison.Ordinal), data.Memory.Series, "no program of the other source");
        Assert.AreEqual(1, processQueries[0].Limit, "the first query asks for one snapshot to find the source");
        Assert.IsGreaterThan(2, processQueries.Length, "the snapshots are read in several pages");
        Assert.IsTrue(processQueries.Skip(1).All(query => query.Source == "agent-a"), "every page query carries the source");
    }

    /// <summary>
    /// After 300 snapshots the charts of the memory and of the connections stop, and each names the time of its last snapshot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderStopsAtTheSnapshotCap()
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var incident = Window(TimeSpan.FromHours(6));

        for (var index = 0; index < 305; index++)
        {
            AddSeven(records, Legacy, _start.AddMinutes(index), 1);
            records.AddConnections(Net, _start.AddMinutes(index), 2, 3);
        }

        // Act
        var data = await new IncidentWindowReader(records).ReadAsync(incident, TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(data.Memory, "the memory chart");
        Assert.IsNotNull(data.Connections, "the connections chart");
        Assert.HasCount(IncidentsView.MaxChartSnapshots, data.Memory.Times, "300 snapshots of memory");
        Assert.AreEqual(_start.AddMinutes(299), data.MemoryCutAt, "the time of the last snapshot of the memory chart");
        Assert.HasCount(IncidentsView.MaxChartConnections, data.Connections.Times, "300 snapshots of connections");
        Assert.AreEqual(_start.AddMinutes(299), data.ConnectionsCutAt, "the time of the last snapshot of the connections chart");
        Assert.IsLessThan(305, records.Queries.Count(query => query.Kind == RecordKind.ProcessSnapshot), "the reader stops instead of reading all 305 snapshots");
        Assert.IsLessThan(305, records.Queries.Count(query => query.Kind == RecordKind.ConnectionSnapshot), "the reader stops instead of reading all 305 connection snapshots");
    }

    /// <summary>
    /// Available memory and swap are charted in MiB at the union of their times with a gap where a series has no point or a value that is not finite, and the connections as the sum of the states.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderChartsAvailableMemorySwapAndConnections()
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var incident = Window(TimeSpan.FromHours(1));

        incident.Metrics.Add(Extreme("mem_available_bytes", MemInfo));
        incident.Metrics.Add(Extreme("swap_used_bytes", MemInfo));
        records.AddMetric(MemInfo, _start.AddMinutes(-1), "mem_available_bytes", 999d * Mib);
        records.AddMetric(MemInfo, _start, "mem_available_bytes", 500d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(1), "mem_available_bytes", 400d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(1), "swap_used_bytes", 10d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(2), "mem_available_bytes", double.NaN);
        records.AddMetric(MemInfo, _start.AddMinutes(3), "swap_used_bytes", 20d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(4), "mem_available_bytes", 200d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(4), "swap_used_bytes", 30d * Mib);
        records.AddMetric(MemInfo, _start.AddMinutes(4), "load1", 8d);
        records.AddMetric("decoy", _start.AddMinutes(5), "mem_available_bytes", 1d);
        records.AddMetric(MemInfo, incident.End, "swap_used_bytes", 999d * Mib);
        records.AddConnections(Net, _start, 3, 4);
        records.AddConnections(Net, _start.AddMinutes(1), 10);

        // Act
        var data = await new IncidentWindowReader(records).ReadAsync(incident, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(data.Memory, "no process snapshot, no memory chart");
        Assert.IsNotNull(data.MemoryAndSwap, "the memory and swap chart");
        Assert.IsFalse(data.MemoryAndSwap.Stacked, "the chart draws lines");
        Assert.AreSequenceEqual(new[] { IncidentsView.AvailableMemory, IncidentsView.SwapUsed }, data.MemoryAndSwap.Series.Select(series => series.Label).ToArray(), "the two series");
        Assert.AreSequenceEqual(Enumerable.Range(0, 5).Select(minute => _start.AddMinutes(minute)).ToArray(), data.MemoryAndSwap.Times.ToArray(), "the union of the times of the two series, inside the window");
        Assert.AreSequenceEqual(new double?[] { 500, 400, null, null, 200 }, data.MemoryAndSwap.Series[0].Values.ToArray(), "available memory in MiB with gaps");
        Assert.AreSequenceEqual(new double?[] { null, 10, null, 20, 30 }, data.MemoryAndSwap.Series[1].Values.ToArray(), "swap in MiB with gaps");
        Assert.IsNotNull(data.Connections, "the connections chart");
        Assert.AreEqual(Net, data.ConnectionsSource, "the source of the connection snapshots");
        Assert.AreSequenceEqual(new[] { IncidentsView.Sockets }, data.Connections.Series.Select(series => series.Label).ToArray(), "one series");
        Assert.AreSequenceEqual(new[] { _start, _start.AddMinutes(1) }, data.Connections.Times.ToArray(), "one point per connection snapshot");
        Assert.AreSequenceEqual(new double?[] { 7, 10 }, data.Connections.Series[0].Values.ToArray(), "the sum of the states");
    }

    /// <summary>
    /// Without data in the window no chart is built, and without a stored extreme of a metric the metric is not read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderReturnsNoChartsWithoutData()
    {
        // Arrange
        var empty = new ScriptedRecordReader();
        var withExtremes = Window(TimeSpan.FromHours(1));
        var withoutExtremes = new ScriptedRecordReader();

        withExtremes.Metrics.Add(Extreme("mem_available_bytes", MemInfo));
        withExtremes.Metrics.Add(Extreme("swap_used_bytes", MemInfo));
        withoutExtremes.AddMetric(MemInfo, _start.AddMinutes(1), "mem_available_bytes", 5d * Mib);
        withoutExtremes.AddMetric(MemInfo, _start.AddMinutes(1), "swap_used_bytes", 5d * Mib);

        // Act
        var nothing = await new IncidentWindowReader(empty).ReadAsync(withExtremes, TestContext.CancellationToken);
        var noExtreme = await new IncidentWindowReader(withoutExtremes).ReadAsync(Window(TimeSpan.FromHours(1)), TestContext.CancellationToken);

        // Assert
        Assert.IsNull(nothing.Memory, "no memory chart");
        Assert.IsNull(nothing.MemoryAndSwap, "no memory and swap chart");
        Assert.IsNull(nothing.Connections, "no connections chart");
        Assert.IsNull(nothing.MemoryCutAt, "no cut of the memory chart");
        Assert.IsNull(nothing.ConnectionsCutAt, "no cut of the connections chart");
        Assert.AreEqual(string.Empty, nothing.MemorySource, "no memory source");
        Assert.AreEqual(string.Empty, nothing.ConnectionsSource, "no connections source");
        Assert.IsNull(noExtreme.MemoryAndSwap, "metrics without a stored extreme give no series");
        Assert.DoesNotContain(query => query.Kind == RecordKind.Metric, withoutExtremes.Queries, "no metric is read without a stored extreme");
    }

    /// <summary>
    /// Each chart reads its own source in every query: the snapshots their source, each metric the source of its stored extreme, the connections their source.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task IncidentWindowReaderTakesTheSourceOfEachChartPerQuery()
    {
        // Arrange
        var records = new ScriptedRecordReader();
        var incident = Window(TimeSpan.FromHours(1));

        incident.Metrics.Add(Extreme("mem_available_bytes", MemInfo));
        incident.Metrics.Add(Extreme("swap_used_bytes", "proc.swap"));

        for (var index = 0; index < 3; index++)
        {
            AddSeven(records, Legacy, _start.AddMinutes(index), 1);
            records.AddMetric(MemInfo, _start.AddMinutes(index), "mem_available_bytes", (100d + index) * Mib);
            records.AddMetric("proc.swap", _start.AddMinutes(index), "swap_used_bytes", (10d + index) * Mib);
            records.AddConnections(Net, _start.AddMinutes(index), 5, 6);
        }

        // Act
        var data = await new IncidentWindowReader(records).ReadAsync(incident, TestContext.CancellationToken);

        // Assert
        var queries = records.Queries;
        var processQueries = queries.Where(query => query.Kind == RecordKind.ProcessSnapshot).ToArray();
        var connectionQueries = queries.Where(query => query.Kind == RecordKind.ConnectionSnapshot).ToArray();
        var metricQueries = queries.Where(query => query.Kind == RecordKind.Metric).ToArray();

        Assert.AreEqual(Legacy, data.MemorySource, "the memory source");
        Assert.AreEqual(Net, data.ConnectionsSource, "the connections source");
        Assert.IsTrue(processQueries.Skip(1).All(query => query.Source == Legacy), "every page of process snapshots carries their source");
        Assert.IsTrue(connectionQueries.Skip(1).All(query => query.Source == Net), "every page of connection snapshots carries their source");
        Assert.IsNotEmpty(metricQueries, "metrics were read");
        Assert.IsTrue(metricQueries.Where(query => query.Name == "mem_available_bytes").All(query => query.Source == MemInfo), "available memory is read from the source of its extreme");
        Assert.IsTrue(metricQueries.Where(query => query.Name == "swap_used_bytes").All(query => query.Source == "proc.swap"), "swap is read from the source of its extreme");
        Assert.IsTrue(metricQueries.All(query => query.Name is "mem_available_bytes" or "swap_used_bytes"), "only the two metrics are read");
        Assert.IsTrue(queries.All(query => query.From >= incident.Start && query.To == incident.End), "every query stays in the window");
        Assert.IsNotNull(data.MemoryAndSwap, "the memory and swap chart");
        Assert.AreSequenceEqual(new double?[] { 100, 101, 102 }, data.MemoryAndSwap.Series[0].Values.ToArray(), "available memory");
        Assert.AreSequenceEqual(new double?[] { 10, 11, 12 }, data.MemoryAndSwap.Series[1].Values.ToArray(), "swap");
    }

    /// <summary>
    /// Creates an incident whose window starts at the base time.
    /// </summary>
    /// <param name="length">The length of the window</param>
    /// <returns>The incident</returns>
    private static Incident Window(TimeSpan length)
    {
        return new Incident
               {
                   Start = _start,
                   End = _start + length,
                   FirstKillAt = _start.AddMinutes(30),
                   LastKillAt = _start.AddMinutes(30),
                   KillCount = 1
               };
    }

    /// <summary>
    /// Creates the stored extreme of a metric.
    /// </summary>
    /// <param name="name">The name of the metric</param>
    /// <param name="source">The source of the metric</param>
    /// <returns>The extreme</returns>
    private static MetricExtreme Extreme(string name, string source)
    {
        return new MetricExtreme
               {
                   Name = name,
                   Extreme = "min",
                   Source = source,
                   At = _start.AddMinutes(10),
                   Value = 1,
                   Samples = 3
               };
    }

    /// <summary>
    /// Adds a snapshot of seven programs p1 to p7 (or with another prefix) that hold the given number of times 10 MiB multiplied by their number.
    /// </summary>
    /// <param name="records">The records</param>
    /// <param name="source">The source</param>
    /// <param name="at">The capture time</param>
    /// <param name="factor">The factor</param>
    /// <param name="prefix">The prefix of the program names</param>
    private static void AddSeven(ScriptedRecordReader records, string source, DateTimeOffset at, ulong factor, string prefix = "p")
    {
        records.AddProcesses(source, at, Enumerable.Range(1, 7).Select(number => ($"{prefix}{number}", (ulong)number * 10 * factor * Mib)).ToArray());
    }

    #endregion // Methods
}