using Vandox.Backend.History;
using Vandox.Backend.Network;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="NetworkAddress"/> and the link of <see cref="NetworkView"/>
/// </summary>
[TestClass]
public class NetworkAddressTests
{
    #region Constants

    private const string BothKinds = "The address gives both a preset range and a start or end time.";
    private const string NeedsBoth = "The address needs both a start and an end time.";
    private const string Reversed = "The address has a start time that is not before its end time.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Every parameter is read, with or without the leading question mark, and keys count without regard to their case.
    /// </summary>
    /// <param name="prefix">The text before the query</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("?")]
    [DataRow("")]
    public void NetworkAddressParseReadsEveryParameter(string prefix)
    {
        // Arrange
        var preset = prefix + "source=agent&Range=7d&AT=2026-10-01T10:00:30";
        var fixedRange = prefix + "Source=import-1&from=2026-10-01T10:00&to=2026-10-02T11:30:15&at=2026-10-01T10:30";

        // Act
        var presetAddress = NetworkAddress.Parse(preset);
        var fixedAddress = NetworkAddress.Parse(fixedRange);

        // Assert
        Assert.AreEqual(string.Empty, presetAddress.Error, "no error");
        Assert.AreEqual("agent", presetAddress.Source, "source");
        Assert.AreEqual(HistoryPreset.SevenDays, presetAddress.Preset, "preset");
        Assert.IsNull(presetAddress.From, "no start");
        Assert.IsNull(presetAddress.To, "no end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 30, TimeSpan.Zero), presetAddress.At, "moment");
        Assert.AreEqual(string.Empty, fixedAddress.Error, "no error for a fixed range");
        Assert.AreEqual("import-1", fixedAddress.Source, "source of the fixed range");
        Assert.IsNull(fixedAddress.Preset, "no preset");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), fixedAddress.From, "start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), fixedAddress.To, "end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.Zero), fixedAddress.At, "moment of the fixed range");
    }

    /// <summary>
    /// Without a parameter, or with empty values, the address has no source or moment and its range is the last day up to now.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("")]
    [DataRow("?")]
    [DataRow("?source=&range=&from=&to=&at=")]
    [DataRow("?metric=system,cpu_percent")]
    public void NetworkAddressParseWithoutParametersIsTheLastDay(string query)
    {
        // Arrange
        var now = HistorySamples.Base;

        // Act
        var address = NetworkAddress.Parse(query);
        var range = address.Range(now);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.IsNull(address.Source, "no source: the default of the charts is shown");
        Assert.IsNull(address.At, "no moment");
        Assert.AreEqual(now.AddDays(-1), range.Start, "the range starts a day before now");
        Assert.AreEqual(now, range.End, "the range ends now");
    }

    /// <summary>
    /// A parameter given twice is an error that names it, whatever the case of its key.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("source=agent&source=agent", "source")]
    [DataRow("source=agent&SOURCE=import", "source")]
    [DataRow("range=1h&range=7d", "range")]
    [DataRow("from=2026-10-01T10:00&from=2026-10-01T11:00&to=2026-10-02T10:00", "from")]
    [DataRow("from=2026-10-01T10:00&to=2026-10-02T10:00&to=2026-10-03T10:00", "to")]
    [DataRow("at=2026-10-01T10:00&at=2026-10-01T10:00", "at")]
    public void NetworkAddressParseRefusesRepeatedParameter(string query, string name)
    {
        // Arrange
        var expected = $"The address gives the parameter {name} more than once.";

        // Act
        var address = NetworkAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter");
    }

    /// <summary>
    /// A value that breaks its rule is an error that names the parameter and never the value, and the first error in the order source, range, from, to, at wins.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("source=a%20b", "source")]
    [DataRow("source=-a", "source")]
    [DataRow("source=/a", "source")]
    [DataRow("source=a%C3%A9", "source")]
    [DataRow("source=a%00", "source")]
    [DataRow("source=agent%0A", "source")]
    [DataRow("source=a+b", "source")]
    [DataRow("source=a,b", "source")]
    [DataRow("range=2h", "range")]
    [DataRow("from=2026-13-01T00:00&to=2026-10-02T00:00", "from")]
    [DataRow("from=2026-10-01T00:00&to=nonsense", "to")]
    [DataRow("at=x", "at")]
    [DataRow("at=2026-10-01T10:00:00.5", "at")]
    [DataRow("source=a%20b&range=2h&at=x", "source")]
    [DataRow("range=2h&from=x&at=x", "range")]
    [DataRow("at=x&from=y", "from")]
    public void NetworkAddressParseRefusesInvalidValue(string query, string name)
    {
        // Arrange
        var expected = InvalidText(name);

        // Act
        var address = NetworkAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter and not its value");
    }

    /// <summary>
    /// A source of 128 bytes is valid and one of 129 bytes is not, and the pattern is checked after the percent-decoding.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressParseChecksTheSourceLengthAndDecodesIt()
    {
        // Arrange
        var longest = new string('a', 128);
        var tooLong = new string('a', 129);

        // Act
        var accepted = NetworkAddress.Parse($"source={longest}");
        var refused = NetworkAddress.Parse($"source={tooLong}");
        var decoded = NetworkAddress.Parse("source=a.b:c/d@e%2Bf-g");
        var encodedLong = NetworkAddress.Parse($"source={string.Concat(Enumerable.Repeat("%61", 129))}");

        // Assert
        Assert.AreEqual(string.Empty, accepted.Error, "128 bytes");
        Assert.AreEqual(longest, accepted.Source, "the source of 128 bytes is kept");
        Assert.AreEqual(InvalidText("source"), refused.Error, "129 bytes");
        Assert.AreEqual(string.Empty, decoded.Error, "the characters of the name pattern");
        Assert.AreEqual("a.b:c/d@e+f-g", decoded.Source, "the percent-decoded source");
        Assert.AreEqual(InvalidText("source"), encodedLong.Error, "129 bytes after decoding");
    }

    /// <summary>
    /// The range rules of the history view hold: a preset with times, one time and a start that is not before the end are errors.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="expected">The error text</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("range=1h&from=2026-10-01T10:00&to=2026-10-02T10:00", BothKinds)]
    [DataRow("range=1h&from=2026-10-01T10:00", BothKinds)]
    [DataRow("range=30d&to=2026-10-02T10:00", BothKinds)]
    [DataRow("from=2026-10-01T10:00", NeedsBoth)]
    [DataRow("to=2026-10-02T10:00", NeedsBoth)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-01T10:00", Reversed)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-02T10:00", Reversed)]
    public void NetworkAddressParseKeepsTheRangeRulesOfTheHistoryView(string query, string expected)
    {
        // Arrange and act
        var address = NetworkAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the range error");
    }

    /// <summary>
    /// Unknown keys are ignored, and <c>compare</c> of the processes view is one of them: the network view has no comparison.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressParseIgnoresUnknownKeysAndCompare()
    {
        // Arrange and act
        var address = NetworkAddress.Parse("?foo=bar&metric=system,cpu_percent&compare=x&compare=y&RANGE=1h&Source=agent");
        var withMoment = NetworkAddress.Parse("at=2026-10-01T10:00&compare=2026-10-01T10:30");

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error, also for a compare value that would not be valid");
        Assert.AreEqual(HistoryPreset.OneHour, address.Preset, "range key of another case");
        Assert.AreEqual("agent", address.Source, "source key of another case");
        Assert.AreEqual(string.Empty, withMoment.Error, "compare needs no moment here");
        Assert.AreEqual("?at=2026-10-01T10:00", withMoment.ToQuery(), "compare is not written");
    }

    /// <summary>
    /// A preset gives the period up to now, and a fixed range gives its own times.
    /// </summary>
    /// <param name="value">The preset in the address</param>
    /// <param name="days">The days of the period</param>
    /// <param name="hours">The hours of the period</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("1h", 0, 1)]
    [DataRow("24h", 1, 0)]
    [DataRow("7d", 7, 0)]
    [DataRow("30d", 30, 0)]
    [DataRow("1y", 365, 0)]
    public void NetworkAddressRangeOfPresetEndsAtNow(string value, int days, int hours)
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 5, 12, 34, 56, 789, TimeSpan.Zero);
        var preset = NetworkAddress.Parse("range=" + value);
        var fixedRange = NetworkAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T11:30:15");

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
    /// The parameters are written in the order source, range, start, end, moment, and exactly in that form.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressToQueryWritesTheParametersInOrder()
    {
        // Arrange
        var preset = NetworkAddress.Parse("at=2026-10-01T10:00:30&range=7d&source=agent");
        var fixedRange = NetworkAddress.Parse("at=2026-10-01T10:00:30&to=2026-10-02T11:30&from=2026-10-01T10:00&source=agent");
        var bare = NetworkAddress.Parse(string.Empty);
        var rangeOnly = NetworkAddress.Parse("range=1y");

        // Act
        var presetQuery = preset.ToQuery();
        var fixedQuery = fixedRange.ToQuery();

        // Assert
        Assert.AreEqual("?source=agent&range=7d&at=2026-10-01T10:00:30", presetQuery, "preset address");
        Assert.AreEqual("?source=agent&from=2026-10-01T10:00&to=2026-10-02T11:30&at=2026-10-01T10:00:30", fixedQuery, "fixed range address");
        Assert.AreEqual(string.Empty, bare.ToQuery(), "an address without parameters is empty");
        Assert.AreEqual("?range=1y", rangeOnly.ToQuery(), "only the range");
    }

    /// <summary>
    /// A source with a character that needs escaping is written escaped and reads back as the same source, the plus sign included.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressToQueryEscapesTheSource()
    {
        // Arrange
        var address = new NetworkAddress
                      {
                          Source = "a+b/c@d:e.f-g",
                          Preset = HistoryPreset.OneDay
                      };

        // Act
        var query = address.ToQuery();
        var back = NetworkAddress.Parse(query);

        // Assert
        Assert.DoesNotContain("a+b", query, "the plus sign is not written as it is");
        Assert.Contains("source=a%2Bb", query, "the plus sign is escaped");
        Assert.AreEqual(string.Empty, back.Error, "the written address is valid");
        Assert.AreEqual("a+b/c@d:e.f-g", back.Source, "the source reads back unchanged");
        Assert.IsTrue(address.SameChart(back), "the same chart");
    }

    /// <summary>
    /// A source outside the name pattern is no address: ToQuery throws.
    /// </summary>
    /// <param name="source">The source</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("sys tem")]
    [DataRow("-agent")]
    [DataRow("agent\n")]
    [DataRow("")]
    public void NetworkAddressToQueryRefusesAnInvalidSource(string source)
    {
        // Arrange
        var address = new NetworkAddress
                      {
                          Source = source,
                          Preset = HistoryPreset.OneDay
                      };
        var tooLong = new NetworkAddress
                      {
                          Source = new string('a', 129)
                      };

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), "a source outside the pattern");
        Assert.ThrowsExactly<InvalidOperationException>(() => tooLong.ToQuery(), "a source of 129 bytes");
    }

    /// <summary>
    /// An error text and a broken range or moment are no address either.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("error")]
    [DataRow("reversed range")]
    [DataRow("preset with times")]
    [DataRow("start without end")]
    [DataRow("moment outside the storable range")]
    [DataRow("start outside the storable range")]
    public void NetworkAddressToQueryRefusesAnInvalidAddress(string scenario)
    {
        // Arrange
        var address = Broken(scenario);

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), $"refused: {scenario}");
    }

    /// <summary>
    /// Two addresses draw the same chart when source, preset, start and end match; the moment does not count.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressSameChartComparesSourcePresetStartAndEnd()
    {
        // Arrange
        var address = NetworkAddress.Parse("source=agent&range=7d&at=2026-10-01T10:00");

        // Act and assert
        Assert.IsTrue(address.SameChart(NetworkAddress.Parse("source=agent&range=7d&at=2026-10-01T11:00")), "another moment");
        Assert.IsTrue(address.SameChart(NetworkAddress.Parse("source=agent&range=7d")), "no moment");
        Assert.IsFalse(address.SameChart(NetworkAddress.Parse("source=import&range=7d")), "another source");
        Assert.IsFalse(address.SameChart(NetworkAddress.Parse("range=7d")), "no source");
        Assert.IsFalse(address.SameChart(NetworkAddress.Parse("source=agent&range=30d")), "another preset");
        Assert.IsFalse(address.SameChart(NetworkAddress.Parse("source=agent&from=2026-10-01T10:00&to=2026-10-02T10:00")), "a fixed range instead of the preset");
        Assert.IsFalse(NetworkAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(NetworkAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:01")), "another end");
        Assert.IsFalse(NetworkAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(NetworkAddress.Parse("from=2026-10-01T10:01&to=2026-10-02T10:00")), "another start");
        Assert.IsTrue(NetworkAddress.Parse(string.Empty).SameChart(NetworkAddress.Parse("?")), "two addresses without parameters");
    }

    /// <summary>
    /// A copy holds the same values and changing it leaves the original unchanged.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressCopyHoldsTheSameValuesApart()
    {
        // Arrange
        var original = NetworkAddress.Parse("source=agent&range=7d&at=2026-10-01T10:00");

        // Act
        var copy = original.Copy();

        copy.Source = "import";
        copy.Preset = HistoryPreset.OneHour;
        copy.At = null;

        // Assert
        Assert.AreEqual("agent", original.Source, "the original keeps its source");
        Assert.AreEqual(HistoryPreset.SevenDays, original.Preset, "the original keeps its preset");
        Assert.IsNotNull(original.At, "the original keeps its moment");

        var same = original.Copy();

        Assert.AreEqual("agent", same.Source, "the copy holds the source");
        Assert.AreEqual(original.At, same.At, "the copy holds the moment");
        Assert.AreEqual(original.Error, same.Error, "the copy holds the error");
    }

    /// <summary>
    /// The link of the page is <c>network</c> with the query of the address, and an invalid address has none.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkViewNetworkHrefAppendsTheQuery()
    {
        // Arrange
        var address = NetworkAddress.Parse("source=agent&range=7d");
        var bare = NetworkAddress.Parse(string.Empty);
        var invalid = NetworkAddress.Parse("range=2h");

        // Act
        var href = NetworkView.NetworkHref(address);
        var bareHref = NetworkView.NetworkHref(bare);

        // Assert
        Assert.AreEqual("network?source=agent&range=7d", href, "the link with the query");
        Assert.AreEqual("network", bareHref, "the link without a query");
        Assert.ThrowsExactly<InvalidOperationException>(() => NetworkView.NetworkHref(invalid), "an invalid address");
        Assert.Throws<ArgumentNullException>(() => NetworkView.NetworkHref(null!), "no address");
    }

    /// <summary>
    /// Parsing no query and comparing with no address are refused.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void NetworkAddressRefusesNullArguments()
    {
        // Arrange
        var address = NetworkAddress.Parse(string.Empty);

        // Act
        var query = Assert.Throws<ArgumentNullException>(() => NetworkAddress.Parse(null!), "no query");
        var other = Assert.Throws<ArgumentNullException>(() => address.SameChart(null!), "no address");

        // Assert
        Assert.AreEqual("query", query.ParamName, "the query parameter");
        Assert.AreEqual("other", other.ParamName, "the other parameter");
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
    /// Creates an address that breaks one rule.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    /// <returns>The address</returns>
    private static NetworkAddress Broken(string scenario)
    {
        var start = HistorySamples.Base;
        var address = new NetworkAddress
                      {
                          Source = "agent",
                          Preset = HistoryPreset.OneDay
                      };

        switch (scenario)
        {
            case "error":
                {
                    address.Error = InvalidText("source");
                }
                break;

            case "reversed range":
                {
                    address.Preset = null;
                    address.From = start.AddDays(1);
                    address.To = start;
                }
                break;

            case "preset with times":
                {
                    address.From = start;
                    address.To = start.AddDays(1);
                }
                break;

            case "start without end":
                {
                    address.Preset = null;
                    address.From = start;
                }
                break;

            case "moment outside the storable range":
                {
                    address.At = StorableTime.Max.AddSeconds(1);
                }
                break;

            default:
                {
                    address.Preset = null;
                    address.From = StorableTime.Min.AddSeconds(-1);
                    address.To = start;
                }
                break;
        }

        return address;
    }

    #endregion // Methods
}