using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the time helpers, the step ladder and the paging of the query engine
/// </summary>
[TestClass]
public class SeriesTimeTests
{
    #region Fields

    private static readonly DateTimeOffset _early = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset _late = new(2026, 10, 1, 11, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// The earlier of two optional instants is the one that exists, or the smaller one, or none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void EarlierReturnsSmallerInstantAndIgnoresNone()
    {
        // Act and assert
        Assert.IsNull(SeriesTime.Earlier(null, null), "two none give none");
        Assert.AreEqual(_late, SeriesTime.Earlier(null, _late), "the second when the first is none");
        Assert.AreEqual(_early, SeriesTime.Earlier(_early, null), "the first when the second is none");
        Assert.AreEqual(_early, SeriesTime.Earlier(_early, _late), "the first when it is smaller");
        Assert.AreEqual(_early, SeriesTime.Earlier(_late, _early), "the second when it is smaller");
        Assert.AreEqual(_early, SeriesTime.Earlier(_early, _early), "either when they are equal");
    }

    /// <summary>
    /// A stored time text is parsed as UTC; a missing, empty or malformed text is no time.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="expected">The expected instant as UTC text, empty for none</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(null, "")]
    [DataRow("", "")]
    [DataRow("not a time", "")]
    [DataRow("2026-10-01T10:00:00", "2026-10-01T10:00:00Z")]
    [DataRow("2026-10-01T12:00:00+02:00", "2026-10-01T10:00:00Z")]
    public void TryParseReadsTimeAsUtc(string? text, string expected)
    {
        // Act
        var parsed = SeriesTime.TryParse(text, out var value);

        // Assert
        Assert.AreEqual(expected.Length > 0, parsed, "whether the text is a time");
        Assert.AreEqual(expected.Length > 0 ? DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture) : default, value, "the instant");
    }

    /// <summary>
    /// A step under an hour is chosen when the policy keeps the five-minute rollups for ever or the range starts inside their period, and not otherwise.
    /// </summary>
    /// <param name="period">Whether the five-minute period is limited</param>
    /// <param name="startDays">The days before the query time the range starts</param>
    /// <param name="expectedMinutes">The expected step in minutes</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow(false, 400, 5)]
    [DataRow(true, 1, 5)]
    [DataRow(true, 400, 60)]
    public void ChooseUsesFiveMinuteStepOnlyWithFiveMinuteRollups(bool period, int startDays, int expectedMinutes)
    {
        // Arrange
        var policy = period ? RetentionSamples.Defaults() : TimeSeriesSamples.Unlimited();
        var now = _early.AddDays(500);
        var from = now.AddDays(-startDays);
        var range = new SeriesRange
                    {
                        From = from,
                        End = from.AddHours(1),
                        Now = now,
                        Target = 100
                    };

        // Act
        var step = SeriesLadder.Choose(range, policy);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(expectedMinutes), step, "the smallest step the rollups can serve");
    }

    /// <summary>
    /// A paged read of an empty window reads nothing and has no last time.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task ReadAsyncOfEmptyWindowReadsNothing()
    {
        // Arrange
        var reads = 0;

        // Act
        var (rows, ranOutAt) = await EventPages.ReadAsync(_ =>
                                                          {
                                                              reads++;

                                                              return Task.FromResult<IReadOnlyList<GapRecordRow>>([]);
                                                          },
                                                          _early,
                                                          _early,
                                                          row => row.CapturedAt,
                                                          row => row.Id,
                                                          TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(0, reads, "no page is asked for");
        Assert.IsEmpty(rows, "no row");
        Assert.IsNull(ranOutAt, "no page was read, so no time was reached");
    }

    #endregion // Methods
}