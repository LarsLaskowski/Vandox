using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkAddressReader"/>
/// </summary>
[TestClass]
public class NetworkAddressReaderTests
{
    #region Fields

    private static readonly HistoryRange _range = new(HistorySamples.Base.AddDays(-1), HistorySamples.Base);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The addresses are read once, for the source, the range and the step of the charts, with the token, and become the table and its notes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task NetworkAddressReaderReadsAtChartStep()
    {
        // Arrange
        var step = TimeSpan.FromMinutes(30);

        using var rollups = new FakeConnectionRollupReader
                            {
                                Addresses = NetworkSamples.Addresses(_range.Start, NetworkSamples.Address("203.0.113.7", 12.34, 40), NetworkSamples.Address("198.51.100.2", 3, 5))
                            };

        // Act
        var addresses = await new NetworkAddressReader(rollups).ReadAsync("agent", _range, step, TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["Addresses"], rollups.Sequence.ToArray(), "one address read and nothing else");
        Assert.AreEqual("agent", rollups.Queries[0].Source, "the source of the charts");
        Assert.AreEqual(_range.Start, rollups.Queries[0].From, "the start of the range");
        Assert.AreEqual(_range.End, rollups.Queries[0].To, "the end of the range");
        Assert.AreEqual(step, rollups.Queries[0].Step, "the step of the charts");
        Assert.AreEqual(TestContext.CancellationToken, rollups.Calls[0].Token, "the token");
        Assert.AreSequenceEqual(["203.0.113.7|12.3|40", "198.51.100.2|3.0|5"], addresses.Table.Rows.Select(NetworkSamples.Cells).ToArray(), "the rows of the table");
        Assert.Contains("Each snapshot counts only its 25 most frequent remote addresses and the rollups keep only the most frequent ones; an address outside them counts 0 there.", addresses.Notes.ToArray(), "the note of the counts");
    }

    /// <summary>
    /// A result with capped periods gives the note of the cap after the note of the counts.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC4")]
    public async Task NetworkAddressReaderNotesCappedPeriods()
    {
        // Arrange
        var plain = NetworkSamples.Addresses(_range.Start, NetworkSamples.Address("203.0.113.7", 1, 1));

        using var rollups = new FakeConnectionRollupReader
                            {
                                Addresses = new ConnectionAddressResult
                                            {
                                                Times = plain.Times,
                                                Gaps = plain.Gaps,
                                                Addresses = plain.Addresses,
                                                Incomplete = [0, 0],
                                                Capped = [0, 2]
                                            }
                            };

        // Act
        var addresses = await new NetworkAddressReader(rollups).ReadAsync("agent", _range, TimeSpan.FromHours(1), TestContext.CancellationToken);

        // Assert
        Assert.HasCount(2, addresses.Notes, "two notes");
        Assert.StartsWith("Some periods in this range held more remote addresses", addresses.Notes[1], "the note of the cap comes second");
    }

    /// <summary>
    /// A failing read is not caught, and no range is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task NetworkAddressReaderLetsAFailureThroughAndRefusesNoRange()
    {
        // Arrange
        using var rollups = new FakeConnectionRollupReader
                            {
                                Failure = new InvalidOperationException("store failed")
                            };

        var reader = new NetworkAddressReader(rollups);

        // Act
        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync("agent", _range, TimeSpan.FromHours(1), TestContext.CancellationToken), "the failure");

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => reader.ReadAsync("agent", null!, TimeSpan.FromHours(1), TestContext.CancellationToken), "no range");

        // Assert
        Assert.AreEqual("store failed", thrown.Message, "the failure of the store");
    }

    #endregion // Methods
}