using Vandox.Backend.Components.Charts;
using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryChartEvents"/>
/// </summary>
[TestClass]
public class HistoryChartEventsTests
{
    #region Constants

    private const double Seconds = 1_800_000_000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A valid selection reaches the callback once, widened outward to whole seconds.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    public async Task SelectReportsValidSelection()
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);
        var whole = DateTimeOffset.FromUnixTimeSeconds((long)Seconds);

        // Act
        await events.Select(Seconds + 0.2, Seconds + 5.7);

        // Assert
        Assert.HasCount(1, recorder.Ranges, "one selection");
        Assert.AreEqual(whole, recorder.Ranges[0].Start, "the start is floored to the second");
        Assert.AreEqual(whole.AddSeconds(6), recorder.Ranges[0].End, "the end is rounded up to the second");
        Assert.IsEmpty(recorder.Picks, "no click");
    }

    /// <summary>
    /// A value that is not finite or outside the storable range, also after widening, and a pair that does not ascend are dropped before the callback.
    /// </summary>
    /// <param name="start">The start in Unix seconds</param>
    /// <param name="end">The end in Unix seconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow(double.NaN, Seconds)]
    [DataRow(Seconds, double.NaN)]
    [DataRow(double.NegativeInfinity, Seconds)]
    [DataRow(Seconds, double.PositiveInfinity)]
    [DataRow(double.PositiveInfinity, double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity, double.NegativeInfinity)]
    [DataRow(-1e300, Seconds)]
    [DataRow(Seconds, 1e300)]
    [DataRow(1e300, 1e301)]
    [DataRow(9_223_372_035.0, 9_223_372_036.3547758)]
    [DataRow(9_223_372_036.5, 9_223_372_040.0)]
    [DataRow(-9_223_372_036.5, 0.0)]
    [DataRow(Seconds + 10, Seconds)]
    [DataRow(Seconds, Seconds)]
    public async Task SelectIgnoresInvalidValues(double start, double end)
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);

        // Act
        await events.Select(start, end);

        // Assert
        Assert.IsEmpty(recorder.Ranges, "no selection reaches the callback");
        Assert.IsEmpty(recorder.Picks, "no click reaches the callback");
    }

    /// <summary>
    /// A valid click reaches the callback once with the time under the pointer and the tolerance.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    public async Task PickReportsValidPick()
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);
        var expected = DateTimeOffset.FromUnixTimeSeconds((long)Seconds).AddMilliseconds(500);

        // Act
        await events.Pick(Seconds + 0.5, 4.5);

        // Assert
        Assert.HasCount(1, recorder.Picks, "one click");
        Assert.AreEqual(expected, recorder.Picks[0].At, "the time under the pointer");
        Assert.AreEqual(TimeSpan.FromSeconds(4.5), recorder.Picks[0].Tolerance, "the tolerance");
        Assert.IsEmpty(recorder.Ranges, "no selection");
    }

    /// <summary>
    /// A tolerance of zero and one of exactly one day are valid.
    /// </summary>
    /// <param name="tolerance">The tolerance in seconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(0.0)]
    [DataRow(86_400.0)]
    public async Task PickAcceptsToleranceFromZeroToOneDay(double tolerance)
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);

        // Act
        await events.Pick(Seconds, tolerance);

        // Assert
        Assert.HasCount(1, recorder.Picks, "the click is reported");
        Assert.AreEqual(TimeSpan.FromSeconds(tolerance), recorder.Picks[0].Tolerance, "the tolerance");
    }

    /// <summary>
    /// A time that is not finite or outside the storable range and a tolerance that is not finite, negative or above one day are dropped before the callback.
    /// </summary>
    /// <param name="at">The time in Unix seconds</param>
    /// <param name="tolerance">The tolerance in seconds</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(double.NaN, 5.0)]
    [DataRow(double.NegativeInfinity, 5.0)]
    [DataRow(double.PositiveInfinity, 5.0)]
    [DataRow(-1e300, 5.0)]
    [DataRow(1e300, 5.0)]
    [DataRow(9_223_372_037.0, 5.0)]
    [DataRow(-9_223_372_036.5, 5.0)]
    [DataRow(Seconds, double.NaN)]
    [DataRow(Seconds, double.NegativeInfinity)]
    [DataRow(Seconds, double.PositiveInfinity)]
    [DataRow(Seconds, -0.001)]
    [DataRow(Seconds, -5.0)]
    [DataRow(Seconds, 86_400.001)]
    [DataRow(Seconds, 1e300)]
    [DataRow(Seconds, -1e300)]
    public async Task PickIgnoresInvalidValues(double at, double tolerance)
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);

        // Act
        await events.Pick(at, tolerance);

        // Assert
        Assert.IsEmpty(recorder.Picks, "no click reaches the callback");
        Assert.IsEmpty(recorder.Ranges, "no selection reaches the callback");
    }

    /// <summary>
    /// After <see cref="HistoryChartEvents.Stop"/> a valid selection and a valid click do nothing, and a second stop does no harm.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC2")]
    [TestCategory("AC8")]
    public async Task StopIgnoresLaterCalls()
    {
        // Arrange
        var recorder = new HistoryChartRecorder();
        var events = new HistoryChartEvents(recorder.SelectedAsync, recorder.PickedAsync);

        await events.Select(Seconds, Seconds + 5);
        await events.Pick(Seconds, 4);

        // Act
        events.Stop();
        events.Stop();

        await events.Select(Seconds, Seconds + 5);
        await events.Pick(Seconds, 4);

        // Assert
        Assert.HasCount(1, recorder.Ranges, "only the selection before the stop was reported");
        Assert.HasCount(1, recorder.Picks, "only the click before the stop was reported");
    }

    #endregion // Methods
}