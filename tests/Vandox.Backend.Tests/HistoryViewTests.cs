using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryView"/>
/// </summary>
[TestClass]
public class HistoryViewTests
{
    #region Methods

    /// <summary>
    /// A selection is widened outward to whole seconds: the start down, the end up.
    /// </summary>
    [TestMethod]
    [TestCategory("AC2")]
    public void SelectionWidensToWholeSeconds()
    {
        // Arrange
        var whole = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        // Act
        var fractions = HistoryView.Selection(1_800_000_000.2, 1_800_000_005.7);
        var wholeSeconds = HistoryView.Selection(1_800_000_000, 1_800_000_005);
        var justAbove = HistoryView.Selection(1_800_000_000.0001, 1_800_000_005.0001);
        var before1970 = HistoryView.Selection(-10.5, -2.5);
        var nearMaximum = HistoryView.Selection(9_223_372_034.5, 9_223_372_035.5);

        // Assert
        Assert.AreEqual(whole, fractions?.Start, "the start is floored");
        Assert.AreEqual(whole.AddSeconds(6), fractions?.End, "the end is rounded up");
        Assert.AreEqual(whole, wholeSeconds?.Start, "a whole start stays");
        Assert.AreEqual(whole.AddSeconds(5), wholeSeconds?.End, "a whole end stays");
        Assert.AreEqual(whole, justAbove?.Start, "a start just above a second is floored");
        Assert.AreEqual(whole.AddSeconds(6), justAbove?.End, "an end just above a second is rounded up");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(-11), before1970?.Start, "a negative start is floored");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(-2), before1970?.End, "a negative end is rounded up");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(9_223_372_034), nearMaximum?.Start, "a start near the maximum");
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(9_223_372_036), nearMaximum?.End, "an end near the maximum that still fits");
    }

    /// <summary>
    /// A value that is not finite or is outside the storable range, also after widening, and a pair that does not ascend give no range.
    /// </summary>
    /// <param name="start">The start in Unix seconds</param>
    /// <param name="end">The end in Unix seconds</param>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow(double.NaN, 1_800_000_000.0)]
    [DataRow(1_800_000_000.0, double.NaN)]
    [DataRow(double.NegativeInfinity, 1_800_000_000.0)]
    [DataRow(1_800_000_000.0, double.PositiveInfinity)]
    [DataRow(double.PositiveInfinity, double.PositiveInfinity)]
    [DataRow(double.NegativeInfinity, double.NegativeInfinity)]
    [DataRow(-1e300, 1_800_000_000.0)]
    [DataRow(1_800_000_000.0, 1e300)]
    [DataRow(1e300, 1e301)]
    [DataRow(9_223_372_035.0, 9_223_372_036.3547758)]
    [DataRow(9_223_372_036.5, 9_223_372_040.0)]
    [DataRow(-9_223_372_036.5, 0.0)]
    [DataRow(1_800_000_010.0, 1_800_000_000.0)]
    public void SelectionRefusesInvalidValues(double start, double end)
    {
        // Arrange and act
        var range = HistoryView.Selection(start, end);

        // Assert
        Assert.IsNull(range, "no range for this selection");
    }

    /// <summary>
    /// A click snaps to the nearest marker within the tolerance, a marker exactly at the tolerance included.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void PickSnapsToTheNearestMarkerWithinTolerance()
    {
        // Arrange
        var at = HistorySamples.Base.AddSeconds(100).AddMilliseconds(900);
        var markers = new[] { Marker(HistorySamples.Base.AddSeconds(90)), Marker(HistorySamples.Base.AddSeconds(104).AddMilliseconds(300)), Marker(HistorySamples.Base.AddSeconds(106)), Marker(HistorySamples.Base.AddSeconds(120)) };

        // Act
        var snapped = HistoryView.Pick(markers, new HistoryPick(at, TimeSpan.FromSeconds(5)));
        var atTolerance = HistoryView.Pick(markers, new HistoryPick(HistorySamples.Base.AddSeconds(111), TimeSpan.FromSeconds(5)));
        var beyond = HistoryView.Pick(markers, new HistoryPick(HistorySamples.Base.AddSeconds(111).AddMilliseconds(1), TimeSpan.FromSeconds(5)));

        // Assert
        Assert.AreEqual(HistorySamples.Base.AddSeconds(104), snapped, "the nearest marker, floored to the second");
        Assert.AreEqual(HistorySamples.Base.AddSeconds(106), atTolerance, "a marker exactly the tolerance away counts");
        Assert.AreEqual(HistorySamples.Base.AddSeconds(111), beyond, "a marker beyond the tolerance leaves the pointer time");
    }

    /// <summary>
    /// With two markers equally near, the earlier one is taken.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void PickTakesTheEarlierMarkerOnATie()
    {
        // Arrange
        var at = HistorySamples.Base.AddSeconds(100);
        var markers = new[] { Marker(at.AddSeconds(3)), Marker(at.AddSeconds(-3)) };

        // Act
        var picked = HistoryView.Pick(markers, new HistoryPick(at, TimeSpan.FromSeconds(5)));
        var reversed = HistoryView.Pick([.. markers.Reverse()], new HistoryPick(at, TimeSpan.FromSeconds(5)));

        // Assert
        Assert.AreEqual(at.AddSeconds(-3), picked, "the earlier marker");
        Assert.AreEqual(at.AddSeconds(-3), reversed, "the earlier marker whatever the order of the list");
    }

    /// <summary>
    /// Without a marker the pointer time is the moment.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void PickUsesThePointerTimeWithoutMarker()
    {
        // Arrange
        var at = HistorySamples.Base.AddSeconds(100).AddMilliseconds(250);

        // Act
        var empty = HistoryView.Pick([], new HistoryPick(at, TimeSpan.FromSeconds(5)));
        var far = HistoryView.Pick([Marker(at.AddMinutes(5))], new HistoryPick(at, TimeSpan.FromSeconds(5)));
        var zeroTolerance = HistoryView.Pick([Marker(at.AddMilliseconds(100))], new HistoryPick(at, TimeSpan.Zero));

        // Assert
        Assert.AreEqual(HistorySamples.Base.AddSeconds(100), empty, "no marker");
        Assert.AreEqual(HistorySamples.Base.AddSeconds(100), far, "a marker far away");
        Assert.AreEqual(HistorySamples.Base.AddSeconds(100), zeroTolerance, "a tolerance of zero takes no marker that is not at the pointer");
    }

    /// <summary>
    /// The moment is floored to the second.
    /// </summary>
    /// <param name="milliseconds">The milliseconds of the pointer time</param>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(500)]
    [DataRow(999)]
    public void PickFloorsToTheSecond(int milliseconds)
    {
        // Arrange
        var at = HistorySamples.Base.AddSeconds(7).AddMilliseconds(milliseconds);

        // Act
        var picked = HistoryView.Pick([], new HistoryPick(at, TimeSpan.FromSeconds(5)));

        // Assert
        Assert.AreEqual(HistorySamples.Base.AddSeconds(7), picked, "floored to the whole second");
    }

    /// <summary>
    /// A whole second stays, any fraction rounds up to the next second, and the maximum stays within the storable range.
    /// </summary>
    /// <param name="ticks">The ticks added to the base</param>
    /// <param name="expectedTicks">The ticks of the expected result added to the base</param>
    [TestMethod]
    [TestCategory("AC8")]
    [DataRow(0L, 0L)]
    [DataRow(10_000_000L, 10_000_000L)]
    [DataRow(1L, 10_000_000L)]
    [DataRow(4_000_000L, 10_000_000L)]
    [DataRow(9_999_999L, 10_000_000L)]
    [DataRow(10_000_001L, 20_000_000L)]
    public void RoundUpToSecondKeepsWholeSeconds(long ticks, long expectedTicks)
    {
        // Arrange
        var time = new DateTimeOffset(HistorySamples.Base.UtcTicks + ticks, TimeSpan.Zero);

        // Act
        var rounded = HistoryView.RoundUpToSecond(time);

        // Assert
        Assert.AreEqual(new DateTimeOffset(HistorySamples.Base.UtcTicks + expectedTicks, TimeSpan.Zero), rounded, "rounded up to the whole second");
    }

    /// <summary>
    /// At the storable maximum, which is no whole second, the result is the last whole second at or below it, and a time below the maximum never passes it.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void RoundUpToSecondClampsAtTheStorableMaximum()
    {
        // Arrange
        var lastSecond = new DateTimeOffset(StorableTime.Max.UtcTicks - (StorableTime.Max.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

        // Act
        var atMaximum = HistoryView.RoundUpToSecond(StorableTime.Max);
        var justBelow = HistoryView.RoundUpToSecond(new DateTimeOffset(StorableTime.Max.UtcTicks - 1, TimeSpan.Zero));
        var inLastFraction = HistoryView.RoundUpToSecond(new DateTimeOffset(lastSecond.UtcTicks + 1, TimeSpan.Zero));

        // Assert
        Assert.AreEqual(lastSecond, atMaximum, "the maximum gives the last whole second");
        Assert.AreEqual(lastSecond, justBelow, "just below the maximum gives the last whole second");
        Assert.AreEqual(lastSecond, inLastFraction, "the last fraction of a second gives the last whole second");
        Assert.IsTrue(StorableTime.Contains(atMaximum), "the result is storable");
    }

    /// <summary>
    /// A step is named in minutes, hours or days with the singular for one.
    /// </summary>
    /// <param name="minutes">The step in minutes</param>
    /// <param name="expected">The text</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(5, "5 minutes")]
    [DataRow(15, "15 minutes")]
    [DataRow(60, "1 hour")]
    [DataRow(120, "2 hours")]
    [DataRow(360, "6 hours")]
    [DataRow(1440, "1 day")]
    [DataRow(4320, "3 days")]
    public void StepTextNamesTheStep(int minutes, string expected)
    {
        // Arrange and act
        var text = HistoryView.StepText(TimeSpan.FromMinutes(minutes));

        // Assert
        Assert.AreEqual(expected, text, "the name of the step");
    }

    /// <summary>
    /// The target is the chart width floored, at most 1,000, and 1 for a width that is not finite or below 1.
    /// </summary>
    /// <param name="width">The width</param>
    /// <param name="expected">The target</param>
    [TestMethod]
    [TestCategory("AC3")]
    [DataRow(0.0, 1)]
    [DataRow(-1.0, 1)]
    [DataRow(-1e300, 1)]
    [DataRow(0.5, 1)]
    [DataRow(double.NaN, 1)]
    [DataRow(double.PositiveInfinity, 1)]
    [DataRow(double.NegativeInfinity, 1)]
    [DataRow(1.0, 1)]
    [DataRow(640.9, 640)]
    [DataRow(1000.0, 1000)]
    [DataRow(1000.9, 1000)]
    [DataRow(1500.0, 1000)]
    [DataRow(1e300, 1000)]
    public void TargetIsTheChartWidthAtMostThousand(double width, int expected)
    {
        // Arrange and act
        var target = HistoryView.Target(width);

        // Assert
        Assert.AreEqual(expected, target, "the target number of points");
    }

    /// <summary>
    /// The metric options keep the entries the address accepts, in the order of the list, and drop the others.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void MetricOptionsDropsEntriesTheAddressRefuses()
    {
        // Arrange
        var list = new StoredMetricList
                   {
                       Metrics = [
                                     new SeriesKey("system", "cpu_percent"),
                                     new SeriesKey("sys tem", "cpu"),
                                     new SeriesKey("system", "with,comma"),
                                     new SeriesKey("system", "line\n"),
                                     new SeriesKey("system", string.Empty),
                                     new SeriesKey("system", new string('a', 129)),
                                     new SeriesKey("-system", "cpu"),
                                     new SeriesKey("system", "ünïcode"),
                                     new SeriesKey("web", "requests.total:5xx/s@host+x-y"),
                                     new SeriesKey("system", new string('b', 128))
                                 ],
                       More = true
                   };

        // Act
        var options = HistoryView.MetricOptions(list);

        // Assert
        Assert.AreEqual($"system cpu_percent|web requests.total:5xx/s@host+x-y|system {new string('b', 128)}", string.Join('|', options.Select(option => $"{option.Source} {option.Name}")), "only accepted metrics, in order");
    }

    /// <summary>
    /// The presets have their address value, label and span.
    /// </summary>
    /// <param name="preset">The preset</param>
    /// <param name="value">The value</param>
    /// <param name="label">The label</param>
    /// <param name="hours">The span in hours</param>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow(HistoryPreset.OneHour, "1h", "1 h", 1)]
    [DataRow(HistoryPreset.OneDay, "24h", "24 h", 24)]
    [DataRow(HistoryPreset.SevenDays, "7d", "7 d", 168)]
    [DataRow(HistoryPreset.ThirtyDays, "30d", "30 d", 720)]
    [DataRow(HistoryPreset.OneYear, "1y", "1 y", 8760)]
    public void PresetsHaveValueLabelAndSpan(HistoryPreset preset, string value, string label, int hours)
    {
        // Arrange and act
        var presetValue = HistoryView.PresetValue(preset);
        var presetLabel = HistoryView.PresetLabel(preset);
        var span = HistoryView.PresetSpan(preset);

        // Assert
        Assert.AreEqual(value, presetValue, "value in the address");
        Assert.AreEqual(label, presetLabel, "label of the button");
        Assert.AreEqual(TimeSpan.FromHours(hours), span, "span of the period");
    }

    /// <summary>
    /// The link is the page with the address written.
    /// </summary>
    [TestMethod]
    [TestCategory("AC8")]
    public void HistoryHrefIsTheHistoryPageWithTheAddress()
    {
        // Arrange
        var address = HistoryAddress.Parse("metric=system,cpu_percent&range=7d&at=2026-10-01T10:00:30");
        var empty = new HistoryAddress();

        // Act
        var href = HistoryView.HistoryHref(address);
        var emptyHref = HistoryView.HistoryHref(empty);

        // Assert
        Assert.AreEqual("history" + address.ToQuery(), href, "history and the query");
        Assert.AreEqual("history", emptyHref, "the page without parameters");
    }

    /// <summary>
    /// Creates a marker.
    /// </summary>
    /// <param name="at">The time</param>
    /// <returns>The marker</returns>
    private static SeriesMarker Marker(DateTimeOffset at)
    {
        return new SeriesMarker
               {
                   Kind = MarkerKind.OomKill,
                   At = at,
                   Text = "php-fpm"
               };
    }

    #endregion // Methods
}