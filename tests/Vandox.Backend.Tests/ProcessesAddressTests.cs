using Vandox.Backend.History;
using Vandox.Backend.Processes;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProcessesAddress"/>
/// </summary>
[TestClass]
public class ProcessesAddressTests
{
    #region Constants

    private const string BothKinds = "The address gives both a preset range and a start or end time.";
    private const string NeedsBoth = "The address needs both a start and an end time.";
    private const string Reversed = "The address has a start time that is not before its end time.";
    private const string NeedsMoment = "The address needs both a moment and a second point for a comparison.";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Every parameter is read, with or without the leading question mark, and keys count without regard to their case.
    /// </summary>
    /// <param name="prefix">The text before the query</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("?")]
    [DataRow("")]
    public void ProcessesAddressParseReadsEveryParameter(string prefix)
    {
        // Arrange
        var preset = prefix + "source=agent&Range=7d&at=2026-10-01T10:00:30&COMPARE=2026-10-01T11:00";
        var fixedRange = prefix + "Source=import-1&from=2026-10-01T10:00&to=2026-10-02T11:30:15&at=2026-10-01T10:30&compare=2026-10-01T10:45:15";

        // Act
        var presetAddress = ProcessesAddress.Parse(preset);
        var fixedAddress = ProcessesAddress.Parse(fixedRange);

        // Assert
        Assert.AreEqual(string.Empty, presetAddress.Error, "no error");
        Assert.AreEqual("agent", presetAddress.Source, "source");
        Assert.AreEqual(HistoryPreset.SevenDays, presetAddress.Preset, "preset");
        Assert.IsNull(presetAddress.From, "no start");
        Assert.IsNull(presetAddress.To, "no end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 30, TimeSpan.Zero), presetAddress.At, "moment");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero), presetAddress.Compare, "second point");
        Assert.AreEqual(string.Empty, fixedAddress.Error, "no error for a fixed range");
        Assert.AreEqual("import-1", fixedAddress.Source, "source of the fixed range");
        Assert.IsNull(fixedAddress.Preset, "no preset");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), fixedAddress.From, "start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), fixedAddress.To, "end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.Zero), fixedAddress.At, "moment of the fixed range");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 45, 15, TimeSpan.Zero), fixedAddress.Compare, "second point of the fixed range");
    }

    /// <summary>
    /// Without a parameter, or with empty values, the address has no source, moment or second point and its range is the last day up to now.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("")]
    [DataRow("?")]
    [DataRow("?source=&range=&from=&to=&at=&compare=")]
    [DataRow("?metric=system,cpu_percent")]
    public void ProcessesAddressParseWithoutParametersIsTheLastDay(string query)
    {
        // Arrange
        var now = HistorySamples.Base;

        // Act
        var address = ProcessesAddress.Parse(query);
        var range = address.Range(now);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.IsNull(address.Source, "no source: the default of the charts is shown");
        Assert.IsNull(address.At, "no moment");
        Assert.IsNull(address.Compare, "no second point");
        Assert.AreEqual(now.AddDays(-1), range.Start, "the range starts a day before now");
        Assert.AreEqual(now, range.End, "the range ends now");
    }

    /// <summary>
    /// A parameter given twice is an error that names it, whatever the case of its key.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("source=agent&source=agent", "source")]
    [DataRow("source=agent&SOURCE=import", "source")]
    [DataRow("range=1h&range=7d", "range")]
    [DataRow("from=2026-10-01T10:00&from=2026-10-01T11:00&to=2026-10-02T10:00", "from")]
    [DataRow("from=2026-10-01T10:00&to=2026-10-02T10:00&to=2026-10-03T10:00", "to")]
    [DataRow("at=2026-10-01T10:00&at=2026-10-01T10:00", "at")]
    [DataRow("at=2026-10-01T10:00&compare=2026-10-01T10:30&compare=2026-10-01T10:30", "compare")]
    [DataRow("at=2026-10-01T10:00&compare=2026-10-01T10:30&COMPARE=2026-10-01T10:31", "compare")]
    public void ProcessesAddressParseRefusesRepeatedParameter(string query, string name)
    {
        // Arrange
        var expected = $"The address gives the parameter {name} more than once.";

        // Act
        var address = ProcessesAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter");
    }

    /// <summary>
    /// A value that breaks its rule is an error that names the parameter and never the value, and the first error in the order source, range, from, to, at, compare wins.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="name">The name in the error</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("source=a%20b", "source")]
    [DataRow("source=-a", "source")]
    [DataRow("source=/a", "source")]
    [DataRow("source=a%C3%A9", "source")]
    [DataRow("source=a%00", "source")]
    [DataRow("source=agent%0A", "source")]
    [DataRow("source=agent%0D", "source")]
    [DataRow("source=a+b", "source")]
    [DataRow("source=a,b", "source")]
    [DataRow("range=2h", "range")]
    [DataRow("from=2026-13-01T00:00&to=2026-10-02T00:00", "from")]
    [DataRow("from=2026-10-01T00:00&to=nonsense", "to")]
    [DataRow("from=2026-10-01T00:00&to=2026-10-02T00:00%0A", "to")]
    [DataRow("at=x", "at")]
    [DataRow("at=2026-10-01T10:00:00.5", "at")]
    [DataRow("at=2026-10-01T10:00&compare=x", "compare")]
    [DataRow("at=2026-10-01T10:00&compare=2026-10-01T10:30:00.5", "compare")]
    [DataRow("source=a%20b&range=2h&at=x&compare=x", "source")]
    [DataRow("range=2h&from=x&at=x&compare=x", "range")]
    [DataRow("at=x&from=y&compare=z", "from")]
    [DataRow("at=x&compare=z", "at")]
    [DataRow("from=2026-10-01T10:00&compare=z", "compare")]
    public void ProcessesAddressParseRefusesInvalidValue(string query, string name)
    {
        // Arrange
        var expected = InvalidText(name);

        // Act
        var address = ProcessesAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error names the parameter and not its value");
    }

    /// <summary>
    /// A source of 128 bytes is valid and one of 129 bytes is not, and the pattern is checked after the percent-decoding.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressParseChecksTheSourceLengthAndDecodesIt()
    {
        // Arrange
        var longest = new string('a', 128);
        var tooLong = new string('a', 129);

        // Act
        var accepted = ProcessesAddress.Parse($"source={longest}");
        var refused = ProcessesAddress.Parse($"source={tooLong}");
        var decoded = ProcessesAddress.Parse("source=a.b:c/d@e%2Bf-g");
        var encodedLong = ProcessesAddress.Parse($"source={string.Concat(Enumerable.Repeat("%61", 129))}");

        // Assert
        Assert.AreEqual(string.Empty, accepted.Error, "128 bytes");
        Assert.AreEqual(longest, accepted.Source, "the source of 128 bytes is kept");
        Assert.AreEqual(InvalidText("source"), refused.Error, "129 bytes");
        Assert.AreEqual(string.Empty, decoded.Error, "the characters of the name pattern");
        Assert.AreEqual("a.b:c/d@e+f-g", decoded.Source, "the percent-decoded source");
        Assert.AreEqual(InvalidText("source"), encodedLong.Error, "129 bytes after decoding");
    }

    /// <summary>
    /// A second point without a moment is an error of its own, after the errors of the values and the range rules.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressParseRefusesCompareWithoutAt()
    {
        // Arrange and act
        var alone = ProcessesAddress.Parse("compare=2026-10-01T10:30");
        var withSource = ProcessesAddress.Parse("source=agent&range=7d&compare=2026-10-01T10:30");
        var withMoment = ProcessesAddress.Parse("at=2026-10-01T10:00&compare=2026-10-01T10:30");
        var badRange = ProcessesAddress.Parse("from=2026-10-02T10:00&to=2026-10-01T10:00&compare=2026-10-01T10:30");
        var badValue = ProcessesAddress.Parse("range=2h&compare=2026-10-01T10:30");

        // Assert
        Assert.AreEqual(NeedsMoment, alone.Error, "a second point alone");
        Assert.AreEqual(NeedsMoment, withSource.Error, "a second point with the other parameters");
        Assert.AreEqual(string.Empty, withMoment.Error, "a moment and a second point");
        Assert.AreEqual(Reversed, badRange.Error, "the range rules come before the comparison rule");
        Assert.AreEqual(InvalidText("range"), badValue.Error, "an invalid value comes before the comparison rule");
    }

    /// <summary>
    /// The range rules of the history view hold: a preset with times, one time and a start that is not before the end are errors.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="expected">The error text</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("range=1h&from=2026-10-01T10:00&to=2026-10-02T10:00", BothKinds)]
    [DataRow("range=1h&from=2026-10-01T10:00", BothKinds)]
    [DataRow("range=30d&to=2026-10-02T10:00", BothKinds)]
    [DataRow("from=2026-10-01T10:00", NeedsBoth)]
    [DataRow("to=2026-10-02T10:00", NeedsBoth)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-01T10:00", Reversed)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-02T10:00", Reversed)]
    public void ProcessesAddressParseKeepsTheRangeRulesOfTheHistoryView(string query, string expected)
    {
        // Arrange and act
        var address = ProcessesAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the range error");
    }

    /// <summary>
    /// Unknown keys are ignored, and the metric of the history view is one of them.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressParseIgnoresUnknownKeys()
    {
        // Arrange and act
        var address = ProcessesAddress.Parse("?foo=bar&metric=system,cpu_percent&Range2=x&RANGE=1h&Source=agent");

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.AreEqual(HistoryPreset.OneHour, address.Preset, "range key of another case");
        Assert.AreEqual("agent", address.Source, "source key of another case");
    }

    /// <summary>
    /// A preset gives the period up to now, and a fixed range gives its own times.
    /// </summary>
    /// <param name="value">The preset in the address</param>
    /// <param name="days">The days of the period</param>
    /// <param name="hours">The hours of the period</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("1h", 0, 1)]
    [DataRow("24h", 1, 0)]
    [DataRow("7d", 7, 0)]
    [DataRow("30d", 30, 0)]
    [DataRow("1y", 365, 0)]
    public void ProcessesAddressRangeOfPresetEndsAtNow(string value, int days, int hours)
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 5, 12, 34, 56, 789, TimeSpan.Zero);
        var preset = ProcessesAddress.Parse("range=" + value);
        var fixedRange = ProcessesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T11:30:15");

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
    /// The parameters are written in the order source, range, start, end, moment, second point, and exactly in that form.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressToQueryWritesTheParametersInOrder()
    {
        // Arrange
        var preset = ProcessesAddress.Parse("compare=2026-10-01T11:00&at=2026-10-01T10:00:30&range=7d&source=agent");
        var fixedRange = ProcessesAddress.Parse("compare=2026-10-01T11:00&at=2026-10-01T10:00:30&to=2026-10-02T11:30&from=2026-10-01T10:00&source=agent");
        var bare = ProcessesAddress.Parse(string.Empty);
        var rangeOnly = ProcessesAddress.Parse("range=1y");

        // Act
        var presetQuery = preset.ToQuery();
        var fixedQuery = fixedRange.ToQuery();

        // Assert
        Assert.AreEqual("?source=agent&range=7d&at=2026-10-01T10:00:30&compare=2026-10-01T11:00", presetQuery, "preset address");
        Assert.AreEqual("?source=agent&from=2026-10-01T10:00&to=2026-10-02T11:30&at=2026-10-01T10:00:30&compare=2026-10-01T11:00", fixedQuery, "fixed range address");
        Assert.AreEqual(string.Empty, bare.ToQuery(), "an address without parameters is empty");
        Assert.AreEqual("?range=1y", rangeOnly.ToQuery(), "only the range");
    }

    /// <summary>
    /// A source with a character that needs escaping is written escaped and reads back as the same source, the plus sign included.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressToQueryEscapesTheSource()
    {
        // Arrange
        var address = new ProcessesAddress
                      {
                          Source = "a+b/c@d:e.f-g",
                          Preset = HistoryPreset.OneDay
                      };

        // Act
        var query = address.ToQuery();
        var back = ProcessesAddress.Parse(query);

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
    [TestCategory("AC4")]
    [DataRow("sys tem")]
    [DataRow("-agent")]
    [DataRow("agent\n")]
    [DataRow("agent\r")]
    [DataRow("")]
    public void ProcessesAddressToQueryRefusesAnInvalidSource(string source)
    {
        // Arrange
        var address = new ProcessesAddress
                      {
                          Source = source,
                          Preset = HistoryPreset.OneDay
                      };
        var tooLong = new ProcessesAddress
                      {
                          Source = new string('a', 129)
                      };
        var accented = new ProcessesAddress
                       {
                           Source = "agent" + (char)0xE9
                       };

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), "a source outside the pattern");
        Assert.ThrowsExactly<InvalidOperationException>(() => tooLong.ToQuery(), "a source of 129 bytes");
        Assert.ThrowsExactly<InvalidOperationException>(() => accented.ToQuery(), "a source with a character outside ASCII");
    }

    /// <summary>
    /// A second point outside the storable range is no address: ToQuery throws.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressToQueryRefusesAnUnstorableCompare()
    {
        // Arrange
        var below = new ProcessesAddress
                    {
                        At = HistorySamples.Base,
                        Compare = StorableTime.Min.AddSeconds(-1)
                    };
        var above = new ProcessesAddress
                    {
                        At = HistorySamples.Base,
                        Compare = StorableTime.Max.AddSeconds(1)
                    };
        var valid = new ProcessesAddress
                    {
                        At = HistorySamples.Base,
                        Compare = HistorySamples.Base.AddMinutes(5)
                    };

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => below.ToQuery(), "a second point before the storable range");
        Assert.ThrowsExactly<InvalidOperationException>(() => above.ToQuery(), "a second point after the storable range");
        Assert.AreEqual("?at=2026-10-01T10:00&compare=2026-10-01T10:05", valid.ToQuery(), "a storable second point is written");
    }

    /// <summary>
    /// A second point without a moment, an error text and a broken range or moment are no address either.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow("error")]
    [DataRow("compare without moment")]
    [DataRow("reversed range")]
    [DataRow("preset with times")]
    [DataRow("start without end")]
    [DataRow("moment outside the storable range")]
    [DataRow("start outside the storable range")]
    public void ProcessesAddressToQueryRefusesAnInvalidAddress(string scenario)
    {
        // Arrange
        var address = Broken(scenario);

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), $"refused: {scenario}");
    }

    /// <summary>
    /// Two addresses draw the same chart when source, preset, start and end match; the moment and the second point do not count.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressSameChartComparesSourcePresetStartAndEnd()
    {
        // Arrange
        var address = ProcessesAddress.Parse("source=agent&range=7d&at=2026-10-01T10:00&compare=2026-10-01T10:30");

        // Act and assert
        Assert.IsTrue(address.SameChart(ProcessesAddress.Parse("source=agent&range=7d&at=2026-10-01T11:00")), "another moment");
        Assert.IsTrue(address.SameChart(ProcessesAddress.Parse("source=agent&range=7d")), "no moment, no second point");
        Assert.IsFalse(address.SameChart(ProcessesAddress.Parse("source=import&range=7d")), "another source");
        Assert.IsFalse(address.SameChart(ProcessesAddress.Parse("range=7d")), "no source");
        Assert.IsFalse(address.SameChart(ProcessesAddress.Parse("source=agent&range=30d")), "another preset");
        Assert.IsFalse(address.SameChart(ProcessesAddress.Parse("source=agent&from=2026-10-01T10:00&to=2026-10-02T10:00")), "a fixed range instead of the preset");
        Assert.IsFalse(ProcessesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(ProcessesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:01")), "another end");
        Assert.IsFalse(ProcessesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T10:00").SameChart(ProcessesAddress.Parse("from=2026-10-01T10:01&to=2026-10-02T10:00")), "another start");
        Assert.IsTrue(ProcessesAddress.Parse(string.Empty).SameChart(ProcessesAddress.Parse("?")), "two addresses without parameters");
    }

    /// <summary>
    /// A copy holds the same values and changing it leaves the original unchanged.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void ProcessesAddressCopyHoldsTheSameValuesApart()
    {
        // Arrange
        var original = ProcessesAddress.Parse("source=agent&range=7d&at=2026-10-01T10:00&compare=2026-10-01T10:30");

        // Act
        var copy = original.Copy();

        copy.Source = "import";
        copy.Preset = HistoryPreset.OneHour;
        copy.At = null;
        copy.Compare = null;

        // Assert
        Assert.AreEqual("agent", original.Source, "the original keeps its source");
        Assert.AreEqual(HistoryPreset.SevenDays, original.Preset, "the original keeps its preset");
        Assert.IsNotNull(original.At, "the original keeps its moment");
        Assert.IsNotNull(original.Compare, "the original keeps its second point");

        var same = original.Copy();

        Assert.AreEqual("agent", same.Source, "the copy holds the source");
        Assert.AreEqual(original.At, same.At, "the copy holds the moment");
        Assert.AreEqual(original.Compare, same.Compare, "the copy holds the second point");
        Assert.AreEqual(original.Error, same.Error, "the copy holds the error");
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
    private static ProcessesAddress Broken(string scenario)
    {
        var start = HistorySamples.Base;
        var address = new ProcessesAddress
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

            case "compare without moment":
                {
                    address.Compare = start;
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