using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the reads of the connection rollups of <see cref="SqliteStore"/>: the states and top programs per step, the top remote addresses and the list of sources
/// </summary>
[TestClass]
public class SqliteStoreConnectionRollupReadTests
{
    #region Constants

    private const string Nginx = "nginx";

    private const string Established = "ESTABLISHED";

    private const string Listen = "LISTEN";

    private const string Udp = "UDP";

    private const string Gap = "gap";

    private const string RangeText = "store: invalid query: from and to must be within the storable range";

    private const string StepText = "store: invalid query: step must be one of the query's steps";

    private const string StepsText = "store: invalid query: the range holds more than 2000 steps";

    private const string FirstAddress = "10.0.0.1";

    private const string UnknownSource = "nobody";

    #endregion // Constants

    #region Fields

    private static readonly TimeSpan _five = TimeSpan.FromMinutes(5);

    private static readonly DateTimeOffset _tierNow = Samples.Base.AddDays(10);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The read returns per step the minimum, average and maximum of every state of the range, ordered by name, with TCP and TCP6 added together and UDP in any state as <c>UDP</c>, and of the programs with the highest sum over
    /// the range, a state or program a snapshot of the step does not name counting 0; the steps lie on the epoch grid and the first holds the start of the range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReturnsStatesAndTopProgramsPerStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1),
                                                                     ConnectionSamples.Source,
                                                                     [
                                                                         ConnectionSamples.State(Proto.Tcp, Established, 2),
                                                                         ConnectionSamples.State(Proto.Tcp6, Established, 1),
                                                                         ConnectionSamples.State(Proto.Tcp, Listen, 1),
                                                                         ConnectionSamples.State(Proto.Udp, string.Empty, 4)
                                                                     ],
                                                                     [ConnectionSamples.Process(1, "a", 5), ConnectionSamples.Process(2, "b", 3), ConnectionSamples.Process(3, "a", 1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(2), ConnectionSamples.Source, [ConnectionSamples.Established(5)], [ConnectionSamples.Process(1, "a", 2), ConnectionSamples.Process(4, "c", 4)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(6), ConnectionSamples.Source, [ConnectionSamples.Established(1)], [ConnectionSamples.Process(1, "a", 1)]));
        await FoldAsync(store);

        // Act
        var result = await store.ConnectionRollupsAsync(Query(Samples.Base.AddMinutes(1), Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base, Samples.Base.AddMinutes(5)], result.Times, "the steps of the grid, the first one holds the start of the range");
        Assert.AreSequenceEqual([Established, Listen, Udp], Names(result.States), "the states in ordinal order, tcp and tcp6 together, udp as UDP");
        Assert.AreSequenceEqual(["4/3/5", "1/1/1"], Points(result.States, Established), "the two snapshots of the first step add 3 and 5 sockets, the second step has one");
        Assert.AreSequenceEqual(["0.5/0/1", "0/0/0"], Points(result.States, Listen), "a state one of two snapshots lists has the minimum 0, a step without it reads 0");
        Assert.AreSequenceEqual(["2/0/4", "0/0/0"], Points(result.States, Udp), "UDP in any state");
        Assert.AreSequenceEqual(["a", "c", "b"], Names(result.Programs), "the programs by their sum over the range: 9, 4 and 3");
        Assert.AreSequenceEqual(["4/2/6", "1/1/1"], Points(result.Programs, "a"), "the sockets of a add up over its processes");
        Assert.AreSequenceEqual(["2/0/4", "0/0/0"], Points(result.Programs, "c"), "c is in one snapshot of the first step");
        Assert.AreSequenceEqual(["1.5/0/3", "0/0/0"], Points(result.Programs, "b"), "b is in one snapshot of the first step");
        Assert.AreSequenceEqual([0.0, 0.0], result.Others, "every program is named, so nothing is left over");
        Assert.AreSequenceEqual([0, 0], result.Incomplete, "no snapshot is incomplete");
    }

    /// <summary>
    /// The read names the 10 programs with the highest sum, ties by name, and returns the other programs as the sum over all processes less the named programs, which includes the programs a snapshot's cut of 50 left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReturnsOthersAsTotalLessNamed()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var processes = Enumerable.Range(1, 60).Select(number => ConnectionSamples.Process(number + 10, string.Create(CultureInfo.InvariantCulture, $"x{number:D2}"), 1)).Prepend(ConnectionSamples.Process(1, "big", 100)).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, processes: processes));
        await FoldAsync(store);

        // Act
        var result = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), _five), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["big", "x01", "x02", "x03", "x04", "x05", "x06", "x07", "x08", "x09"], Names(result.Programs), "the 10 programs with the highest sum, the tie of count 1 by name");
        Assert.AreSequenceEqual(["100/100/100"], Points(result.Programs, "big"), "the largest program");
        Assert.AreSequenceEqual(["1/1/1"], Points(result.Programs, "x09"), "the last named program");
        Assert.AreSequenceEqual([51.0], result.Others, "the sum of all 160 sockets less the 109 of the named programs, the cut programs included");
    }

    /// <summary>
    /// A step without a folded snapshot is a gap in every line and in the other programs, a step with snapshots that lack a state or program reads 0, and the incomplete snapshots of each step are counted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReturnsGapsAndCompleteness()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(2)], [ConnectionSamples.Process(1, "a", 2)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(11), ConnectionSamples.Source, [ConnectionSamples.Established(4)], [ConnectionSamples.Process(1, "a", 4)], incomplete: true),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(12), ConnectionSamples.Source, [ConnectionSamples.State(Proto.Udp, string.Empty, 1)], [ConnectionSamples.Process(2, "b", 1)], incomplete: true));
        await FoldAsync(store);

        // Act
        var result = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), _five), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base, Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10)], result.Times, "three steps");
        Assert.AreSequenceEqual(["2/2/2", Gap, "2/0/4"], Points(result.States, Established), "the empty middle step is a gap, the last step averages 4 and 0");
        Assert.AreSequenceEqual(["0/0/0", Gap, "0.5/0/1"], Points(result.States, Udp), "a state the first step's snapshot does not list reads 0");
        Assert.AreSequenceEqual(["2/2/2", Gap, "2/0/4"], Points(result.Programs, "a"), "the programs follow the same steps");
        Assert.AreSequenceEqual([0.0, null, 0.0], result.Others, "the other programs are a gap in the middle step");
        Assert.AreSequenceEqual([0, 0, 2], result.Incomplete, "the two snapshots marked aggregates_incomplete are counted in their step");
    }

    /// <summary>
    /// The result says up to when the source's connection snapshots are folded through the earliest capture time of those not yet folded, and whether the range starts before the oldest bucket of the tier read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReturnsEarliestUnfoldedAndStartsBeforeOldestBucket()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var hour = TimeSpan.FromHours(1);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddSeconds(5), "other", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(1)]));

        var beforeFold = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);

        await FoldAsync(store);
        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(10), "other", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(30), ConnectionSamples.Source, [ConnectionSamples.Established(1)]));

        // Act
        var pending = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);

        await FoldAsync(store);

        var folded = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);
        var earlier = await store.ConnectionRollupsAsync(Query(Samples.Base.AddMinutes(-10), Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);
        var later = await store.ConnectionRollupsAsync(Query(Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);
        var hourlyEarlier = await store.ConnectionRollupsAsync(Query(Samples.Base.AddHours(-1), Samples.Base.AddHours(1), hour), TestContext.CancellationToken);
        var hourlyAt = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddHours(1), hour), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(Samples.Base.AddMinutes(1), beforeFold.EarliestUnfolded, "before the fold: the earliest snapshot of the source, not that of the other source");
        Assert.AreEqual(Samples.Base.AddMinutes(30), pending.EarliestUnfolded, "a snapshot stored after the fold is the earliest not yet folded");
        Assert.IsNull(folded.EarliestUnfolded, "after the fold nothing is left");
        Assert.IsTrue(earlier.StartsBeforeBuckets, "the range starts before the oldest 5-minute bucket");
        Assert.IsFalse(folded.StartsBeforeBuckets, "the range starts at the oldest 5-minute bucket");
        Assert.IsFalse(later.StartsBeforeBuckets, "the range starts after it");
        Assert.IsTrue(hourlyEarlier.StartsBeforeBuckets, "the same for the hourly tier");
        Assert.IsFalse(hourlyAt.StartsBeforeBuckets, "an hourly range starting at the oldest hourly bucket does not start before it");
    }

    /// <summary>
    /// A step below one hour reads the 5-minute buckets, which a range reaching back before their period gives as gaps with the starts-before flag set, a range wholly before it as no lines; a step of one hour reads the
    /// hourly buckets, which still hold that range.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReadsFiveMinuteTierBelowOneHour()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var half = TimeSpan.FromMinutes(30);

        await FoldTiersAsync(store);

        // Act
        var across = await store.ConnectionRollupsAsync(Query(_tierNow.AddHours(-26), _tierNow, half), TestContext.CancellationToken);
        var before = await store.ConnectionRollupsAsync(Query(_tierNow.AddDays(-4), _tierNow.AddDays(-2), half), TestContext.CancellationToken);
        var hourly = await store.ConnectionRollupsAsync(Query(_tierNow.AddDays(-3), _tierNow.AddDays(-2), TimeSpan.FromHours(1)), TestContext.CancellationToken);

        // Assert
        var index = across.Times.ToList().IndexOf(_tierNow.AddHours(-1));
        var points = Points(across.States, Established);

        Assert.HasCount(52, across.Times, "26 hours in steps of 30 minutes");
        Assert.AreEqual(50, index, "the step that holds the recent snapshot");
        Assert.AreEqual("3/3/3", points[index], "the 5-minute bucket still holds the snapshot");
        Assert.HasCount(1, points.Where(point => point != Gap).ToList(), "the steps before the 5-minute buckets are gaps");
        Assert.AreEqual("3/3/3", Points(across.Programs, Nginx)[index], "the program of the same bucket");
        Assert.IsTrue(across.StartsBeforeBuckets, "the range starts before the oldest 5-minute bucket");
        Assert.IsEmpty(before.States, "a range wholly before the 5-minute buckets has no state lines");
        Assert.IsEmpty(before.Programs, "and no program lines");
        Assert.IsTrue(before.StartsBeforeBuckets, "and starts before them");
        Assert.AreEqual("1/1/1", Points(hourly.States, Established)[0], "the hourly bucket still holds the old snapshot");
        Assert.AreEqual("1/1/1", Points(hourly.Programs, Nginx)[0], "and its program");
        Assert.IsFalse(hourly.StartsBeforeBuckets, "the range does not start before the oldest hourly bucket");
    }

    /// <summary>
    /// A step spanning several buckets combines them: the average is the sum over all snapshots of the step, the minimum 0 where fewer snapshots name the item than the step holds, the maximum the largest of the buckets.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsCombinesBucketsOfOneStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(2)], [ConnectionSamples.Process(1, "a", 2)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(11), ConnectionSamples.Source, [ConnectionSamples.Established(6)]));
        await FoldAsync(store);

        // Act
        var result = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base], result.Times, "one step of 15 minutes");
        Assert.AreSequenceEqual(["4/2/6"], Points(result.States, Established), "the two buckets give the average 4 of the two snapshots, the smallest and the largest");
        Assert.AreSequenceEqual(["1/0/2"], Points(result.Programs, "a"), "a program one of the two snapshots lists has the minimum 0");
        Assert.AreSequenceEqual([0.0], result.Others, "nothing is left over");
    }

    /// <summary>
    /// An unknown source, an empty range, a reversed range and a range without a bucket return an empty result of either read without an error.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsReturnsEmptyResult()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(1)], [ConnectionSamples.Process(1, Nginx, 1)], [ConnectionSamples.Remote(FirstAddress, 1)]));
        await FoldAsync(store);

        var queries = new List<(ConnectionRollupQuery Query, string Why)>
                      {
                          (Query(Samples.Base, Samples.Base.AddMinutes(10), _five, UnknownSource), "an unknown source"),
                          (Query(Samples.Base, Samples.Base, _five), "an empty range"),
                          (Query(Samples.Base.AddMinutes(10), Samples.Base, _five), "a range that ends before it starts"),
                          (Query(Samples.Base.AddHours(5), Samples.Base.AddHours(6), _five), "a range without a bucket")
                      };

        // Act
        var states = new List<ConnectionRollupResult>();
        var addresses = new List<ConnectionAddressResult>();

        foreach (var (query, _) in queries)
        {
            states.Add(await store.ConnectionRollupsAsync(query, TestContext.CancellationToken));
            addresses.Add(await store.ConnectionAddressesAsync(query, TestContext.CancellationToken));
        }

        // Assert
        for (var index = 0; index < queries.Count; index++)
        {
            AssertEmpty(states[index], queries[index].Why);
            AssertEmpty(addresses[index], queries[index].Why);
        }

        Assert.IsNull(states[3].EarliestUnfolded, "nothing is waiting to be folded");
        Assert.IsFalse(states[3].StartsBeforeBuckets, "the range starts after the oldest bucket");
        Assert.IsNull(addresses[3].EarliestUnfolded, "nothing is waiting to be folded for the addresses either");
        Assert.IsFalse(addresses[3].StartsBeforeBuckets, "the range starts after the oldest bucket for the addresses too");
    }

    /// <summary>
    /// A query with a step that is not one of the query's steps, more than 2,000 steps counted on the epoch grid or a bound outside the storable range is refused with the exact text, the step checked first and the
    /// range before the number of steps; the largest valid ranges are not refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC5")]
    public async Task SqliteStoreConnectionRollupsRefusesInvalidQuery()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var refused = RefusedQueries();

        // Act
        var refusals = new List<string>();

        foreach (var (query, text, why) in refused)
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ConnectionRollupsAsync(query, TestContext.CancellationToken), why);

            Assert.AreEqual(text, exception.Message, why);
            refusals.Add(exception.Message);
        }

        var results = new List<ConnectionRollupResult>();

        foreach (var (query, _) in AcceptedQueries())
        {
            results.Add(await store.ConnectionRollupsAsync(query, TestContext.CancellationToken));
        }

        // Assert
        Assert.AreSequenceEqual(refused.Select(entry => entry.Text).ToList(), refusals, "each invalid query is refused with its exact text");
        Assert.HasCount(AcceptedQueries().Count, results, "every valid query returns a result");
    }

    /// <summary>
    /// The address read returns the 20 addresses with the highest average over the range, ties by address text, each with its highest count in one snapshot, and the number of further addresses the range's buckets hold;
    /// a snapshot outside the range counts in neither.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionAddressesReturnsTopTwentyWithFurtherCount()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, remotes: ConnectionSamples.Ranked(0, 25)),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(6), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(1, 1, 5, 100)),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(20), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote("10.0.9.9", 999)]));
        await FoldAsync(store);

        // Act
        var both = await store.ConnectionAddressesAsync(Query(Samples.Base, Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);
        var second = await store.ConnectionAddressesAsync(Query(Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10), _five), TestContext.CancellationToken);
        var first = await store.ConnectionAddressesAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), _five), TestContext.CancellationToken);

        // Assert
        var expected = Enumerable.Range(1, 5).Select(octet => string.Create(CultureInfo.InvariantCulture, $"10.0.1.{octet}|50|100")).Concat(Enumerable.Range(11, 15).Reverse().Select(octet => string.Create(CultureInfo.InvariantCulture, $"10.0.0.{octet}|{octet / 2.0}|{octet}"))).ToList();

        Assert.AreSequenceEqual(expected, Describe(both), "five addresses with an average of 50 over the two snapshots, then those of the first snapshot by count");
        Assert.AreEqual(10L, both.Further, "30 addresses in the range, 20 returned; the address of the snapshot outside the range is not counted");
        Assert.AreSequenceEqual(Enumerable.Range(1, 5).Select(octet => string.Create(CultureInfo.InvariantCulture, $"10.0.1.{octet}|100|100")).ToList(), Describe(second), "a range of one snapshot averages over that snapshot");
        Assert.AreEqual(0L, second.Further, "five addresses fit in the top 20");
        Assert.AreSequenceEqual(Enumerable.Range(6, 20).Reverse().Select(octet => string.Create(CultureInfo.InvariantCulture, $"10.0.0.{octet}|{octet}|{octet}")).ToList(), Describe(first), "the 20 highest of 25");
        Assert.AreEqual(5L, first.Further, "five addresses are left out");
    }

    /// <summary>
    /// The address read returns per step whether it is a gap, the incomplete snapshots and the snapshots that lost addresses to the cap of 500 per 5-minute bucket.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionAddressesReturnsGapsIncompleteAndCappedPerStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var records = Enumerable.Range(0, 21).Select(third => ConnectionSamples.Snapshot(Samples.Base.AddSeconds(third * 10), ConnectionSamples.Source, remotes: ConnectionSamples.Remotes(third, 1, 25, 1))).ToList();

        records.Add(ConnectionSamples.Snapshot(Samples.Base.AddMinutes(6), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote("10.9.0.1", 7)], incomplete: true));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. records]);
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddMinutes(12), TestContext.CancellationToken);

        // Act
        var result = await store.ConnectionAddressesAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), _five), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([Samples.Base, Samples.Base.AddMinutes(5), Samples.Base.AddMinutes(10)], result.Times, "three steps");
        Assert.AreSequenceEqual([false, false, true], result.Gaps, "the last step has no folded snapshot");
        Assert.AreSequenceEqual([0, 1, 0], result.Incomplete, "the incomplete snapshot is counted in its step");
        Assert.AreSequenceEqual([1, 0, 0], result.Capped, "the 21st snapshot of 25 new addresses lost them to the cap of the first bucket");
        Assert.HasCount(20, result.Addresses, "the top 20 are returned");
        Assert.AreEqual(string.Create(CultureInfo.InvariantCulture, $"10.9.0.1|{7.0 / 22}|7"), Describe(result)[0], "the address with the highest sum comes first, averaged over the 22 snapshots of the range");
    }

    /// <summary>
    /// The address read returns the earliest unfolded capture time of the source and whether the range starts before the oldest bucket of the tier read, a step below one hour reading the 5-minute buckets and a step of one
    /// hour the hourly ones.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionAddressesReturnsCompletenessAndReadsTheTierOfTheStep()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var half = TimeSpan.FromMinutes(30);

        await FoldTiersAsync(store);
        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(_tierNow.AddMinutes(-30), ConnectionSamples.Source, remotes: [ConnectionSamples.Remote(FirstAddress, 1)]), ConnectionSamples.Snapshot(_tierNow.AddMinutes(-20), "other", remotes: [ConnectionSamples.Remote(FirstAddress, 1)]));

        // Act
        var across = await store.ConnectionAddressesAsync(Query(_tierNow.AddHours(-26), _tierNow, half), TestContext.CancellationToken);
        var before = await store.ConnectionAddressesAsync(Query(_tierNow.AddDays(-4), _tierNow.AddDays(-2), half), TestContext.CancellationToken);
        var hourly = await store.ConnectionAddressesAsync(Query(_tierNow.AddDays(-3), _tierNow.AddDays(-2), TimeSpan.FromHours(1)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([$"{FirstAddress}|3|3"], Describe(across), "the 5-minute bucket of the recent snapshot, the unfolded one is not in it");
        Assert.AreEqual(0L, across.Further, "one address");
        Assert.IsTrue(across.StartsBeforeBuckets, "the range starts before the oldest 5-minute bucket");
        Assert.AreEqual(_tierNow.AddMinutes(-30), across.EarliestUnfolded, "the earliest snapshot of the source that is not folded, not that of the other source");
        Assert.IsEmpty(before.Addresses, "a range wholly before the 5-minute buckets has no addresses");
        Assert.IsTrue(before.StartsBeforeBuckets, "and starts before them");
        Assert.AreSequenceEqual([$"{FirstAddress}|1|1"], Describe(hourly), "the hourly bucket still holds the old snapshot");
        Assert.IsFalse(hourly.StartsBeforeBuckets, "the range does not start before the oldest hourly bucket");
    }

    /// <summary>
    /// The address read refuses an invalid query with the same exact texts and order as the states read.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionAddressesRefusesInvalidQuery()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var refused = RefusedQueries();

        // Act
        var refusals = new List<string>();

        foreach (var (query, text, why) in refused)
        {
            var exception = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ConnectionAddressesAsync(query, TestContext.CancellationToken), why);

            Assert.AreEqual(text, exception.Message, why);
            refusals.Add(exception.Message);
        }

        var results = new List<ConnectionAddressResult>();

        foreach (var (query, _) in AcceptedQueries())
        {
            results.Add(await store.ConnectionAddressesAsync(query, TestContext.CancellationToken));
        }

        // Assert
        Assert.AreSequenceEqual(refused.Select(entry => entry.Text).ToList(), refusals, "each invalid query is refused with its exact text");
        Assert.HasCount(AcceptedQueries().Count, results, "every valid query returns a result");
    }

    /// <summary>
    /// The source list returns each source with a connection bucket of either tier in the range, by name, with the start of its latest bucket within the range; a bucket after the range and a source with program rollups only
    /// are left out.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionSourcesListsLatestBucketInRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), "web", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddHours(2).AddMinutes(7), "web", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddHours(5), "web", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddMinutes(30).AddSeconds(10), "db", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(Samples.Base.AddHours(5).AddMinutes(1), "late", [ConnectionSamples.Established(1)]),
                                          ProgramSamples.Snapshot(Samples.Base.AddMinutes(2), "programs", ProgramSamples.Program(Nginx, 1, 100, 1)));
        await FoldAsync(store);

        // Act
        var list = await store.ConnectionSourcesAsync(Sources(Samples.Base, Samples.Base.AddHours(3)), TestContext.CancellationToken);
        var empty = await store.ConnectionSourcesAsync(Sources(Samples.Base.AddDays(2), Samples.Base.AddDays(3)), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["db", "web"], list.Sources.Select(source => source.Source).ToList(), "the sources with a connection bucket in the range, by name");
        Assert.AreSequenceEqual([Samples.Base.AddMinutes(30), Samples.Base.AddHours(2).AddMinutes(5)], list.Sources.Select(source => source.LatestBucket).ToList(), "the latest bucket of each source within the range");
        Assert.IsFalse(list.More, "the list is complete");
        Assert.IsEmpty(empty.Sources, "a range without a bucket lists no source");
        Assert.IsFalse(empty.More, "and has no more");
    }

    /// <summary>
    /// A source whose 5-minute buckets are past their period but whose hourly buckets are in the range is listed with the start of its latest bucket of either tier.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionSourcesListsSourceOfTheHourlyTierOnly()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(Samples.Base.AddDays(8).AddMinutes(7), "old", [ConnectionSamples.Established(1)]),
                                          ConnectionSamples.Snapshot(_tierNow.AddMinutes(-54), "new", [ConnectionSamples.Established(1)]));
        await RetentionSamples.FoldAllAsync(store, TierPolicy(), _tierNow, TestContext.CancellationToken);

        // Act
        var list = await store.ConnectionSourcesAsync(Sources(Samples.Base.AddDays(8), _tierNow), TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["new", "old"], list.Sources.Select(source => source.Source).ToList(), "both sources, by name");
        Assert.AreSequenceEqual([_tierNow.AddMinutes(-55), Samples.Base.AddDays(8)], list.Sources.Select(source => source.LatestBucket).ToList(), "the 5-minute bucket of the recent source, the hourly bucket of the old one");
    }

    /// <summary>
    /// The source list holds at most 256 sources and says when there are more.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionSourcesReturnsAtMost256WithMore()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var query = Sources(Samples.Base, Samples.Base.AddHours(1));

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, [.. Enumerable.Range(0, StorageLimits.MaxProgramSources).Select(SourceSnapshot)]);
        await FoldAsync(store);

        var exact = await store.ConnectionSourcesAsync(query, TestContext.CancellationToken);

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, SourceSnapshot(StorageLimits.MaxProgramSources));
        await FoldAsync(store);

        // Act
        var capped = await store.ConnectionSourcesAsync(query, TestContext.CancellationToken);

        // Assert
        Assert.HasCount(StorageLimits.MaxProgramSources, exact.Sources, "256 sources fit");
        Assert.IsFalse(exact.More, "and are not more than the list holds");
        Assert.HasCount(StorageLimits.MaxProgramSources, capped.Sources, "257 sources are cut to 256");
        Assert.IsTrue(capped.More, "the list says there are more");
        Assert.AreEqual("s255", capped.Sources[^1].Source, "the first 256 by name are kept");
    }

    /// <summary>
    /// A source query with a bound outside the storable range is refused with the exact text.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC6")]
    public async Task SqliteStoreConnectionSourcesRejectsRangeOutsideStorableRange()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var start = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ConnectionSourcesAsync(Sources(DateTimeOffset.MinValue, Samples.Base), TestContext.CancellationToken), "a start outside the range");
        var end = await Assert.ThrowsExactlyAsync<InvalidQueryException>(() => store.ConnectionSourcesAsync(Sources(Samples.Base, DateTimeOffset.MaxValue), TestContext.CancellationToken), "an end outside the range");

        // Assert
        Assert.AreEqual(RangeText, start.Message, "the text for the start");
        Assert.AreEqual(RangeText, end.Message, "the text for the end");
    }

    /// <summary>
    /// Sums that overflow a double (forged in the stored buckets) are clamped in both reads: every average, minimum, maximum and other-programs value is finite.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task SqliteStoreConnectionRollupsClampsSumsInRead()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        int[] minutes = [1, 6, 11];
        var snapshots = minutes.Select(minute => ConnectionSamples.Snapshot(Samples.Base.AddMinutes(minute), ConnectionSamples.Source, [ConnectionSamples.Established(1)], [ConnectionSamples.Process(1, "a", 1)], [ConnectionSamples.Remote(FirstAddress, 1)])).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, snapshots);
        await FoldAsync(store);
        await RetentionSamples.ExecuteAsync(directory.Path, "UPDATE connection_rollups_5m SET sum_value = 1.7976931348623157e308; UPDATE connection_rollup_totals_5m SET process_sum = 1.7976931348623157e308", TestContext.CancellationToken);

        // Act
        var states = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15)), TestContext.CancellationToken);
        var addresses = await store.ConnectionAddressesAsync(Query(Samples.Base, Samples.Base.AddMinutes(15), TimeSpan.FromMinutes(15)), TestContext.CancellationToken);

        // Assert
        var values = states.States.Concat(states.Programs).SelectMany(line => line.Points).Where(point => point is not null).SelectMany(point => new[] { point!.Value.Average, point.Value.Minimum, point.Value.Maximum }).Concat(states.Others.Select(other => other ?? 0)).Concat(addresses.Addresses.SelectMany(address => new[] { address.Average, address.Maximum })).ToList();

        Assert.IsNotEmpty(values, "the reads hold values");
        Assert.IsTrue(values.All(double.IsFinite), "every value is finite");
        Assert.AreEqual(double.MaxValue / 3, states.States[0].Points[0]!.Value.Average, "the clamped sum of three snapshots is divided by their number");
        Assert.AreEqual(double.MaxValue / 3, states.Programs[0].Points[0]!.Value.Average, "the same for the program");
        Assert.AreEqual(double.MaxValue / 3, addresses.Addresses[0].Average, "the same for the address");
        Assert.AreEqual(0.0, states.Others[0], "the other programs are the clamped total less the named program, never negative");
    }

    /// <summary>
    /// Hostile commands of up to 1,024 bytes come back as received, each ranked with its own values: quotes, a percent sign, SQL text, a NUL and other control characters, a right-to-left override and an emoji.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task SqliteStoreConnectionRollupsReturnsHostileCommandsAsStored()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        string[] names = ["\u0000", "\u0001", "\u202E", "\U0001F600", "q\"u'o\\te", "100%_", "x'); DROP TABLE records; --", new string('a', 1024)];
        var processes = names.Select((name, index) => ConnectionSamples.Process(index + 1, name, (uint)((names.Length - index) * 1000))).ToArray();

        await RetentionSamples.WriteAsync(store, TestContext.CancellationToken, ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), ConnectionSamples.Source, processes: processes));
        await FoldAsync(store);

        // Act
        var result = await store.ConnectionRollupsAsync(Query(Samples.Base, Samples.Base.AddMinutes(5), _five), TestContext.CancellationToken);
        var kept = await RetentionSamples.CountAsync(directory.Path, "SELECT count(*) FROM sqlite_master WHERE name = 'records'", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(names, Names(result.Programs), "every command comes back as received, ranked by its sockets");

        for (var index = 0; index < names.Length; index++)
        {
            var value = ((names.Length - index) * 1000).ToString(CultureInfo.InvariantCulture);

            Assert.AreSequenceEqual([$"{value}/{value}/{value}"], Points(result.Programs, names[index]), $"the values of the command at rank {index}");
        }

        Assert.AreSequenceEqual([0.0], result.Others, "no program is left out");
        Assert.AreEqual(1L, kept, "the SQL text in a command is not run");
    }

    /// <summary>
    /// Creates a query of the test source.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="step">The step</param>
    /// <param name="source">The source</param>
    /// <returns>The query</returns>
    private static ConnectionRollupQuery Query(DateTimeOffset from, DateTimeOffset to, TimeSpan step, string source = ConnectionSamples.Source)
    {
        return new ConnectionRollupQuery
               {
                   Source = source,
                   From = from,
                   To = to,
                   Step = step
               };
    }

    /// <summary>
    /// Creates a source query.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The query</returns>
    private static ProgramSourceQuery Sources(DateTimeOffset from, DateTimeOffset to)
    {
        return new ProgramSourceQuery
               {
                   From = from,
                   To = to
               };
    }

    /// <summary>
    /// Gets the invalid queries with the text each is refused with.
    /// </summary>
    /// <returns>The queries, the texts and the cases</returns>
    private static List<(ConnectionRollupQuery Query, string Text, string Why)> RefusedQueries()
    {
        var from = Samples.Base;
        var to = Samples.Base.AddHours(1);

        return
        [
            (Query(from, to, TimeSpan.FromMinutes(7)), StepText, "a step of 7 minutes"),
            (Query(from, to, TimeSpan.Zero), StepText, "a step of zero"),
            (Query(from, to, TimeSpan.FromHours(4)), StepText, "a step of 4 hours"),
            (Query(from, to, TimeSpan.FromHours(36)), StepText, "a step that is not whole days"),
            (Query(from, from.AddDays(365), _five), StepsText, "a year at 5 minutes"),
            (Query(from.AddMinutes(1), from.AddMinutes(1) + (2000 * _five), _five), StepsText, "2,000 steps that overlap 2,001 steps of the epoch grid"),
            (Query(DateTimeOffset.MinValue, to, _five), RangeText, "a start outside the storable range"),
            (Query(from, DateTimeOffset.MaxValue, _five), RangeText, "an end outside the storable range"),
            (Query(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, _five), RangeText, "a range outside the storable range is refused before its steps are counted"),
            (Query(DateTimeOffset.MinValue, to, TimeSpan.FromMinutes(7)), StepText, "a wrong step is refused before the range is checked")
        ];
    }

    /// <summary>
    /// Gets the valid queries at the limits.
    /// </summary>
    /// <returns>The queries and the cases</returns>
    private static List<(ConnectionRollupQuery Query, string Why)> AcceptedQueries()
    {
        var from = Samples.Base;

        return
        [
            (Query(from, from + (2000 * _five), _five), "2,000 steps on the epoch grid"),
            (Query(from, from.AddDays(6), TimeSpan.FromDays(3)), "whole days"),
            (Query(from, from.AddHours(1), TimeSpan.FromHours(12)), "12 hours")
        ];
    }

    /// <summary>
    /// Gets the names of the lines, in order.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The names</returns>
    private static List<string> Names(IReadOnlyList<ConnectionRollupLine> lines)
    {
        return [.. lines.Select(line => line.Name)];
    }

    /// <summary>
    /// Describes the points of a line as average, minimum and maximum joined with slashes, a gap as <c>gap</c>.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="name">The name of the line</param>
    /// <returns>The points as text</returns>
    private static List<string> Points(IReadOnlyList<ConnectionRollupLine> lines, string name)
    {
        var line = lines.Single(candidate => candidate.Name == name);

        return [.. line.Points.Select(DescribePoint)];
    }

    /// <summary>
    /// Describes one point.
    /// </summary>
    /// <param name="point">The point, <c>null</c> for a gap</param>
    /// <returns>The text</returns>
    private static string DescribePoint(ProgramRollupPoint? point)
    {
        return point is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value.Average}/{value.Minimum}/{value.Maximum}") : Gap;
    }

    /// <summary>
    /// Describes the addresses of a result as address, average and maximum joined with bars.
    /// </summary>
    /// <param name="result">The result</param>
    /// <returns>The addresses as text, in order</returns>
    private static List<string> Describe(ConnectionAddressResult result)
    {
        return [.. result.Addresses.Select(address => string.Create(CultureInfo.InvariantCulture, $"{address.Address}|{address.Average}|{address.Maximum}"))];
    }

    /// <summary>
    /// Creates the snapshot of the source with the given number, in the first bucket.
    /// </summary>
    /// <param name="number">The number</param>
    /// <returns>The record</returns>
    private static DataRecord SourceSnapshot(int number)
    {
        return ConnectionSamples.Snapshot(Samples.Base.AddMinutes(1), string.Create(CultureInfo.InvariantCulture, $"s{number:D3}"), [ConnectionSamples.Established(1)]);
    }

    /// <summary>
    /// Creates the policy of the tier tests: the 5-minute buckets last a day, the hourly buckets a year.
    /// </summary>
    /// <returns>The policy</returns>
    private static RetentionPolicy TierPolicy()
    {
        return new RetentionPolicy
               {
                   Raw = RetentionSamples.Days(1),
                   FiveMinuteRollups = RetentionSamples.Days(1),
                   HourlyRollups = RetentionSamples.Days(365),
                   Logs = RetentionSamples.Days(1),
                   Snapshots = RetentionSamples.Days(1),
                   Events = RetentionSamples.Days(365)
               };
    }

    /// <summary>
    /// Checks that a result of the states read has no lines, steps or counts.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="why">The case</param>
    private static void AssertEmpty(ConnectionRollupResult result, string why)
    {
        Assert.IsEmpty(result.States, $"{why}: no state lines");
        Assert.IsEmpty(result.Programs, $"{why}: no program lines");
        Assert.IsEmpty(result.Times, $"{why}: no steps");
        Assert.IsEmpty(result.Others, $"{why}: no other programs");
        Assert.IsEmpty(result.Incomplete, $"{why}: no incomplete counts");
    }

    /// <summary>
    /// Checks that a result of the address read has no addresses, steps or counts.
    /// </summary>
    /// <param name="result">The result</param>
    /// <param name="why">The case</param>
    private static void AssertEmpty(ConnectionAddressResult result, string why)
    {
        Assert.IsEmpty(result.Addresses, $"{why}: no addresses");
        Assert.AreEqual(0L, result.Further, $"{why}: no further addresses");
        Assert.IsEmpty(result.Times, $"{why}: no steps");
        Assert.IsEmpty(result.Gaps, $"{why}: no gaps");
        Assert.IsEmpty(result.Incomplete, $"{why}: no incomplete counts");
        Assert.IsEmpty(result.Capped, $"{why}: no capped counts");
    }

    /// <summary>
    /// Folds every stored snapshot with the default periods, a day after the first bucket.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that completes when everything is folded</returns>
    private async Task FoldAsync(SqliteStore store)
    {
        await RetentionSamples.FoldAllAsync(store, RetentionSamples.Defaults(), Samples.Base.AddDays(1), TestContext.CancellationToken);
    }

    /// <summary>
    /// Folds an old and a recent snapshot of the test source with the periods of <see cref="TierPolicy"/> at <c>_tierNow</c>: the old one only into the hourly tier, the recent one (3 sockets) into both.
    /// </summary>
    /// <param name="store">The store</param>
    /// <returns>A task that completes when everything is folded</returns>
    private async Task FoldTiersAsync(SqliteStore store)
    {
        await RetentionSamples.WriteAsync(store,
                                          TestContext.CancellationToken,
                                          ConnectionSamples.Snapshot(_tierNow.AddDays(-3).AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(1)], [ConnectionSamples.Process(1, Nginx, 1)], [ConnectionSamples.Remote(FirstAddress, 1)]),
                                          ConnectionSamples.Snapshot(_tierNow.AddHours(-1).AddMinutes(1), ConnectionSamples.Source, [ConnectionSamples.Established(3)], [ConnectionSamples.Process(1, Nginx, 3)], [ConnectionSamples.Remote(FirstAddress, 3)]));
        await RetentionSamples.FoldAllAsync(store, TierPolicy(), _tierNow, TestContext.CancellationToken);
    }

    #endregion // Methods
}