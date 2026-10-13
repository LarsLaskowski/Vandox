using Vandox.Backend.Incidents;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="RecordPager"/>
/// </summary>
[TestClass]
public class RecordPagerTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Records that share one capture time are all handed over once, in the order of their IDs, across pages.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task RecordPagerPagesThroughRecordsThatShareATime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var recording = new RecordingRecordReader(store);
        var seen = new List<long>();
        var range = new RecordQuery
                    {
                        Kind = RecordKind.LogLine,
                        From = StorableTime.Min,
                        To = StorableTime.Max
                    };

        await IncidentSamples.WriteAsync(store, TestContext.CancellationToken, Enumerable.Range(0, 7).Select(index => IncidentSamples.Line(IncidentSamples.Base, "cron", $"job {index} finished")).ToArray());

        // Act
        await RecordPager.PageAsync(recording,
                                    range,
                                    3,
                                    record =>
                                    {
                                        seen.Add(record.Id);

                                        return Task.CompletedTask;
                                    },
                                    TestContext.CancellationToken);

        // Assert
        Assert.HasCount(7, seen, "every record once");
        Assert.AreSequenceEqual(seen.Order().ToList(), seen, "in the order of the IDs");
        Assert.HasCount(7, seen.Distinct().ToList(), "no record twice");
        Assert.HasCount(3, recording.Queries, "pages of 3, 3 and 1");
        Assert.IsTrue(recording.Queries.All(query => query.Limit == 3), "the page size is the limit");
        Assert.AreEqual(0, recording.Queries[0].AfterId, "the first page starts at the beginning");
        Assert.AreEqual(seen[2], recording.Queries[1].AfterId, "the second page starts after the third record");
        Assert.AreEqual(seen[5], recording.Queries[2].AfterId, "the third page starts after the sixth record");
        Assert.IsTrue(recording.Queries.Skip(1).All(query => query.From == IncidentSamples.Base), "the pages continue at the shared time");
    }

    /// <summary>
    /// A page that leads back to the same record is an error and stops the paging.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task RecordPagerFailsWhenPageDoesNotAdvance()
    {
        // Arrange
        var reader = new RepeatingRecordReader();
        var range = new RecordQuery
                    {
                        Kind = RecordKind.LogLine,
                        From = StorableTime.Min,
                        To = StorableTime.Max
                    };

        // Act
        var exception = await Assert.ThrowsExactlyAsync<StoreException>(() => RecordPager.PageAsync(reader, range, 3, _ => Task.CompletedTask, TestContext.CancellationToken), "the page does not advance");

        // Assert
        Assert.AreEqual("store: reading records: page did not advance", exception.Message, "message");
        Assert.IsLessThan(5, reader.Calls, "the paging stops after the page repeated");
    }

    #endregion // Methods
}