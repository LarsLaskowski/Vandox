using Vandox.Backend.History;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="HistoryAddress"/>
/// </summary>
[TestClass]
public class HistoryAddressTests
{
    #region Constants

    private const string Reversed = "The address has a start time that is not before its end time.";
    private const string TooMany = "The address names more than 8 metrics.";
    private const string BothKinds = "The address gives both a preset range and a start or end time.";
    private const string NeedsBoth = "The address needs both a start and an end time.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Every parameter is read, with or without the leading question mark, and keys count without regard to their case.
    /// </summary>
    /// <param name="prefix">The text before the query</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("?")]
    [DataRow("")]
    public void ParseReadsEveryParameter(string prefix)
    {
        // Arrange
        var preset = prefix + "metric=system,mem_available_bytes&Metric=SYSTEM,cpu_percent&range=7d&at=2026-10-01T10:00:30";
        var fixedRange = prefix + "metric=system,cpu_percent&from=2026-10-01T10:00&to=2026-10-02T11:30:15";

        // Act
        var presetAddress = HistoryAddress.Parse(preset);
        var fixedAddress = HistoryAddress.Parse(fixedRange);

        // Assert
        Assert.AreEqual(string.Empty, presetAddress.Error, "no error");
        Assert.AreEqual("system mem_available_bytes|SYSTEM cpu_percent", Describe(presetAddress), "the metrics in the order of the address");
        Assert.AreEqual(HistoryPreset.SevenDays, presetAddress.Preset, "preset");
        Assert.IsNull(presetAddress.From, "no start");
        Assert.IsNull(presetAddress.To, "no end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 30, TimeSpan.Zero), presetAddress.At, "moment");
        Assert.AreEqual(string.Empty, fixedAddress.Error, "no error for a fixed range");
        Assert.IsNull(fixedAddress.Preset, "no preset");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), fixedAddress.From, "start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), fixedAddress.To, "end");
        Assert.IsNull(fixedAddress.At, "no moment");
    }

    /// <summary>
    /// Without range, start and end the address is the last day up to now, and an empty value is no value.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("")]
    [DataRow("?")]
    [DataRow("?range=&from=&to=&at=&metric=")]
    [DataRow("?metric=system,cpu_percent")]
    public void ParseWithoutRangeIsTheLastDay(string query)
    {
        // Arrange
        var now = HistorySamples.Base;

        // Act
        var address = HistoryAddress.Parse(query);
        var range = address.Range(now);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.IsNull(address.At, "no moment");
        Assert.AreEqual(now.AddDays(-1), range.Start, "the range starts a day before now");
        Assert.AreEqual(now, range.End, "the range ends now");
    }

    /// <summary>
    /// A parameter given twice is an error that names it; metric may repeat, and keys of another case count as the same key.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("range=1h&range=7d", "range")]
    [DataRow("range=1h&RANGE=1h", "range")]
    [DataRow("from=2026-10-01T10:00&from=2026-10-01T11:00&to=2026-10-02T10:00", "from")]
    [DataRow("from=2026-10-01T10:00&to=2026-10-02T10:00&to=2026-10-03T10:00", "to")]
    [DataRow("at=2026-10-01T10:00&at=2026-10-01T10:00", "at")]
    [DataRow("metric=a,b&at=2026-10-01T10:00&AT=2026-10-01T10:01", "at")]
    public void ParseRefusesRepeatedParameter(string query, string name)
    {
        // Arrange
        var expected = $"The address gives the parameter {name} more than once.";

        // Act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter");
    }

    /// <summary>
    /// A value that breaks its rule is an error that names the parameter and never the value, and the first error in the order metric, range, from, to, at wins.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("metric=cpu", "metric")]
    [DataRow("metric=a,b,c", "metric")]
    [DataRow("metric=a,", "metric")]
    [DataRow("metric=,b", "metric")]
    [DataRow("metric=a%20b,c", "metric")]
    [DataRow("metric=-a,b", "metric")]
    [DataRow("metric=a,b%C3%A9", "metric")]
    [DataRow("metric=a,b%2Fc%00", "metric")]
    [DataRow("metric=system,cpu_percent%0A", "metric")]
    [DataRow("metric=system,cpu_percent%0D", "metric")]
    [DataRow("metric=system%0A,cpu_percent", "metric")]
    [DataRow("range=2h", "range")]
    [DataRow("from=2026-13-01T00:00&to=2026-10-02T00:00", "from")]
    [DataRow("from=2026-10-01T00:00&to=nonsense", "to")]
    [DataRow("from=2026-10-01T00:00&to=2026-10-02T00:00%0A", "to")]
    [DataRow("at=x", "at")]
    [DataRow("at=2026-10-01T10:00:00.5", "at")]
    [DataRow("metric=cpu&range=2h&at=x", "metric")]
    [DataRow("range=2h&from=x&at=x", "range")]
    [DataRow("at=x&from=y", "from")]
    public void ParseRefusesInvalidValue(string query, string name)
    {
        // Arrange
        var expected = InvalidText(name);

        // Act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter and not its value");
    }

    /// <summary>
    /// A part of 128 bytes is valid and one of 129 bytes is not.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ParseRefusesInvalidValueOfMoreThan128Bytes()
    {
        // Arrange
        var longest = new string('a', 128);
        var tooLong = new string('a', 129);

        // Act
        var accepted = HistoryAddress.Parse($"metric={longest},{longest}");
        var refusedSource = HistoryAddress.Parse($"metric={tooLong},cpu");
        var refusedName = HistoryAddress.Parse($"metric=system,{tooLong}");

        // Assert
        Assert.AreEqual(string.Empty, accepted.Error, "128 bytes per part");
        Assert.AreEqual(InvalidText("metric"), refusedSource.Error, "129 bytes in the source");
        Assert.AreEqual(InvalidText("metric"), refusedName.Error, "129 bytes in the name");
    }

    /// <summary>
    /// A ninth distinct metric is refused with the count error.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ParseRefusesMoreThanEightMetrics()
    {
        // Arrange
        var eight = string.Join('&', Enumerable.Range(1, 8).Select(index => $"metric=system,m{index}"));

        // Act
        var accepted = HistoryAddress.Parse(eight);
        var refused = HistoryAddress.Parse(eight + "&metric=system,m9");

        // Assert
        Assert.AreEqual(string.Empty, accepted.Error, "eight metrics");
        Assert.HasCount(8, accepted.Metrics, "eight metrics are kept");
        Assert.AreEqual(TooMany, refused.Error, "nine metrics");
    }

    /// <summary>
    /// A metric named more than once counts once and keeps its first place, and an empty metric value is skipped.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ParseUsesRepeatedMetricOnce()
    {
        // Arrange
        var query = "metric=system,b&metric=system,a&metric=&metric=system,b&metric=system,a&metric=system,c&metric=system,b&metric=system,d&metric=system,e&metric=system,f&metric=system,g&metric=system,h";

        // Act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "eight distinct metrics are no error however often they are named");
        Assert.AreEqual("system b|system a|system c|system d|system e|system f|system g|system h", Describe(address), "first order, each once");
    }

    /// <summary>
    /// A preset together with a start or an end time is an error.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("range=1h&from=2026-10-01T10:00&to=2026-10-02T10:00")]
    [DataRow("range=1h&from=2026-10-01T10:00")]
    [DataRow("range=30d&to=2026-10-02T10:00")]
    public void ParseRefusesPresetWithTimes(string query)
    {
        // Arrange and act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(BothKinds, address.Error, "preset and times");
    }

    /// <summary>
    /// A start without an end, or an end without a start, is an error.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("from=2026-10-01T10:00")]
    [DataRow("to=2026-10-02T10:00")]
    public void ParseRefusesOneTime(string query)
    {
        // Arrange and act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(NeedsBoth, address.Error, "one time");
    }

    /// <summary>
    /// A start that is not before the end is an error.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("from=2026-10-02T10:00&to=2026-10-01T10:00")]
    [DataRow("from=2026-10-02T10:00&to=2026-10-02T10:00")]
    public void ParseRefusesReversedRange(string query)
    {
        // Arrange and act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(Reversed, address.Error, "start not before end");
    }

    /// <summary>
    /// Unknown keys are ignored and known keys are read without regard to their case.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ParseIgnoresUnknownKeysAndCase()
    {
        // Arrange
        var query = "?foo=bar&METRIC=system,cpu_percent&RANGE=1h&Range2=x&At=2026-10-01T10:00";

        // Act
        var address = HistoryAddress.Parse(query);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual("system cpu_percent", Describe(address), "metric key of another case");
        Assert.AreEqual(HistoryPreset.OneHour, address.Preset, "range key of another case");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), address.At, "at key of another case");
    }

    /// <summary>
    /// A written address reads back to the same metrics, range and moment.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("")]
    [DataRow("?metric=system,cpu_percent")]
    [DataRow("?metric=system,cpu_percent&metric=system,mem_available_bytes&range=30d&at=2026-10-01T10:00:30")]
    [DataRow("?metric=a.b:c/d@e+f-g,h&from=2026-10-01T10:00:05&to=2026-10-02T11:30&at=2026-10-01T10:00")]
    [DataRow("?range=1y")]
    public void ToQueryReadsBackTheSameAddress(string query)
    {
        // Arrange
        var original = HistoryAddress.Parse(query);

        // Act
        var written = original.ToQuery();
        var back = HistoryAddress.Parse(written);

        // Assert
        Assert.AreEqual(string.Empty, back.Error, "the written address is valid");
        Assert.IsTrue(original.SameChart(back), "the same metrics and range");
        Assert.AreEqual(original.At, back.At, "the same moment");
        Assert.AreEqual(Describe(original), Describe(back), "the same metric order");
    }

    /// <summary>
    /// The parameters are written in the order metric, range, start, end, moment.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ToQueryWritesTheParametersInOrder()
    {
        // Arrange
        var preset = HistoryAddress.Parse("at=2026-10-01T10:00:30&range=7d&metric=system,a&metric=system,b");
        var fixedRange = HistoryAddress.Parse("at=2026-10-01T10:00:30&to=2026-10-02T11:30&from=2026-10-01T10:00&metric=system,a");

        // Act
        var presetQuery = preset.ToQuery();
        var fixedQuery = fixedRange.ToQuery();

        // Assert
        Assert.IsTrue(IsBefore(presetQuery, "metric=", "range=7d"), "metric before range");
        Assert.IsTrue(IsBefore(presetQuery, "range=7d", "at=2026-10-01T10:00:30"), "range before at");
        Assert.AreEqual(2, presetQuery.Split("metric=").Length - 1, "one parameter per metric");
        Assert.IsTrue(IsBefore(fixedQuery, "from=2026-10-01T10:00", "to=2026-10-02T11:30"), "from before to");
        Assert.IsTrue(IsBefore(fixedQuery, "to=2026-10-02T11:30", "at="), "to before at");
        Assert.DoesNotContain("range=", fixedQuery, "no preset beside times");
    }

    /// <summary>
    /// A value the checks refuse is no address: ToQuery throws.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("error")]
    [DataRow("metric outside the pattern")]
    [DataRow("metric ending in a line feed")]
    [DataRow("metric ending in a carriage return")]
    [DataRow("source of 129 bytes")]
    [DataRow("name of 129 bytes")]
    [DataRow("empty name")]
    [DataRow("nine metrics")]
    [DataRow("reversed range")]
    [DataRow("equal start and end")]
    [DataRow("preset with times")]
    [DataRow("start without end")]
    [DataRow("end without start")]
    [DataRow("start outside the storable range")]
    [DataRow("moment outside the storable range")]
    public void ToQueryRefusesInvalidValues(string scenario)
    {
        // Arrange
        var address = Invalid(scenario);

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), $"refused: {scenario}");
    }

    /// <summary>
    /// A preset gives the period up to now, and a fixed range gives its own times.
    /// </summary>
    /// <param name="value">The preset in the address</param>
    /// <param name="days">The days of the period</param>
    /// <param name="hours">The hours of the period</param>
    [TestMethod]
    [TestCategory("AC2")]
    [DataRow("1h", 0, 1)]
    [DataRow("24h", 1, 0)]
    [DataRow("7d", 7, 0)]
    [DataRow("30d", 30, 0)]
    [DataRow("1y", 365, 0)]
    public void RangeOfPresetEndsAtNow(string value, int days, int hours)
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 5, 12, 34, 56, 789, TimeSpan.Zero);
        var preset = HistoryAddress.Parse("range=" + value);
        var fixedRange = HistoryAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T11:30:15");

        // Act
        var range = preset.Range(now);
        var fixedResult = fixedRange.Range(now);

        // Assert
        Assert.AreEqual(now, range.End, "the preset ends at now");
        Assert.AreEqual(now - new TimeSpan(days, hours, 0, 0), range.Start, "the preset starts its span before now");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), fixedResult.Start, "a fixed range keeps its start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), fixedResult.End, "a fixed range keeps its end");
    }

    /// <summary>
    /// Two addresses draw the same chart when metrics, preset, start and end match; the moment does not count.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void SameChartComparesMetricsPresetStartAndEnd()
    {
        // Arrange
        var address = HistoryAddress.Parse("metric=system,a&metric=system,b&range=7d&at=2026-10-01T10:00");

        // Act and assert
        Assert.IsTrue(address.SameChart(HistoryAddress.Parse("metric=system,a&metric=system,b&range=7d&at=2026-10-01T11:00")), "another moment");
        Assert.IsTrue(address.SameChart(HistoryAddress.Parse("metric=system,a&metric=system,b&range=7d")), "no moment");
        Assert.IsFalse(address.SameChart(HistoryAddress.Parse("metric=system,b&metric=system,a&range=7d")), "another metric order");
        Assert.IsFalse(address.SameChart(HistoryAddress.Parse("metric=system,a&range=7d")), "fewer metrics");
        Assert.IsFalse(address.SameChart(HistoryAddress.Parse("metric=system,a&metric=system,b&range=30d")), "another preset");
        Assert.IsFalse(address.SameChart(HistoryAddress.Parse("metric=system,a&metric=system,b&from=2026-10-01T10:00&to=2026-10-02T10:00")), "a fixed range instead of the preset");
        Assert.IsFalse(HistoryAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(HistoryAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:01")), "another end");
        Assert.IsFalse(HistoryAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(HistoryAddress.Parse("from=2026-10-01T10:01&to=2026-10-02T10:00")), "another start");
    }

    /// <summary>
    /// A copy holds the same values and changing it leaves the original unchanged.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void CopyHoldsTheSameValuesApart()
    {
        // Arrange
        var original = HistoryAddress.Parse("metric=system,a&range=7d&at=2026-10-01T10:00");

        // Act
        var copy = original.Copy();

        copy.Preset = HistoryPreset.OneHour;
        copy.At = null;

        // Assert
        Assert.AreEqual("system a", Describe(copy), "the metrics");
        Assert.AreEqual(HistoryPreset.SevenDays, original.Preset, "the original keeps its preset");
        Assert.IsNotNull(original.At, "the original keeps its moment");
    }

    /// <summary>
    /// Gives the error text of an invalid value.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    private static string InvalidText(string name)
    {
        return $"The address has an invalid value for the parameter {name}.";
    }

    /// <summary>
    /// Tells whether a text is found before another in a query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="first">The text expected first</param>
    /// <param name="second">The text expected second</param>
    /// <returns><c>true</c> when both are found and the first starts earlier</returns>
    private static bool IsBefore(string query, string first, string second)
    {
        var start = query.IndexOf(first, StringComparison.Ordinal);

        return start >= 0 && start < query.IndexOf(second, StringComparison.Ordinal);
    }

    /// <summary>
    /// Describes the metrics of an address as <c>source name</c> joined by a bar.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The text</returns>
    private static string Describe(HistoryAddress address)
    {
        return string.Join('|', address.Metrics.Select(metric => $"{metric.Source} {metric.Name}"));
    }

    /// <summary>
    /// Creates a valid address.
    /// </summary>
    /// <returns>The address</returns>
    private static HistoryAddress Valid()
    {
        return new HistoryAddress
               {
                   Metrics = [new SeriesKey("system", "cpu_percent")],
                   Preset = HistoryPreset.OneDay
               };
    }

    /// <summary>
    /// Creates an address that breaks one rule.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <returns>The address</returns>
    private static HistoryAddress Invalid(string scenario)
    {
        var address = Valid();
        var start = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        switch (scenario)
        {
            case "error":
                address.Error = "The address has an invalid value for the parameter metric.";
                break;

            case "metric outside the pattern":
                address.Metrics = [new SeriesKey("sys tem", "cpu")];
                break;

            case "metric ending in a line feed":
                address.Metrics = [new SeriesKey("system", "cpu_percent\n")];
                break;

            case "metric ending in a carriage return":
                address.Metrics = [new SeriesKey("system", "cpu_percent\r")];
                break;

            case "source of 129 bytes":
                address.Metrics = [new SeriesKey(new string('a', 129), "cpu")];
                break;

            case "name of 129 bytes":
                address.Metrics = [new SeriesKey("system", new string('a', 129))];
                break;

            case "empty name":
                address.Metrics = [new SeriesKey("system", string.Empty)];
                break;

            case "nine metrics":
                address.Metrics = [.. Enumerable.Range(1, 9).Select(index => new SeriesKey("system", $"m{index}"))];
                break;

            default:
                return InvalidRange(address, scenario, start);
        }

        return address;
    }

    /// <summary>
    /// Creates an address with an invalid range or moment.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="scenario">The scenario</param>
    /// <param name="start">A start time</param>
    /// <returns>The address</returns>
    private static HistoryAddress InvalidRange(HistoryAddress address, string scenario, DateTimeOffset start)
    {
        address.Preset = null;

        switch (scenario)
        {
            case "reversed range":
                {
                    address.From = start.AddDays(1);
                    address.To = start;
                }
                break;

            case "equal start and end":
                {
                    address.From = start;
                    address.To = start;
                }
                break;

            case "preset with times":
                {
                    address.Preset = HistoryPreset.OneDay;
                    address.From = start;
                    address.To = start.AddDays(1);
                }
                break;

            case "start without end":
                {
                    address.From = start;
                }
                break;

            case "end without start":
                {
                    address.To = start;
                }
                break;

            case "start outside the storable range":
                {
                    address.From = StorableTime.Min.AddSeconds(-1);
                    address.To = start;
                }
                break;

            default:
                {
                    address.Preset = HistoryPreset.OneDay;
                    address.At = StorableTime.Max.AddSeconds(1);
                }
                break;
        }

        return address;
    }

    #endregion // Methods
}