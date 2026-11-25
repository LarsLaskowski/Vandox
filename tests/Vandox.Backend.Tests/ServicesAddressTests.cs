using Vandox.Backend.History;
using Vandox.Backend.ServiceHealth;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ServicesAddress"/>
/// </summary>
[TestClass]
public class ServicesAddressTests
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
    [TestCategory("AC1")]
    [DataRow("?")]
    [DataRow("")]
    public void ServicesAddressParsesRangeFromToAt(string prefix)
    {
        // Arrange
        var preset = prefix + "Range=7d&AT=2026-10-01T10:00:30";
        var fixedRange = prefix + "from=2026-10-01T10:00&To=2026-10-02T11:30:15&at=2026-10-01T10:30";

        // Act
        var presetAddress = ServicesAddress.Parse(preset);
        var fixedAddress = ServicesAddress.Parse(fixedRange);

        // Assert
        Assert.AreEqual(string.Empty, presetAddress.Error, "no error");
        Assert.AreEqual(HistoryPreset.SevenDays, presetAddress.Times.Preset, "preset");
        Assert.IsNull(presetAddress.Times.From, "no start");
        Assert.IsNull(presetAddress.Times.To, "no end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 30, TimeSpan.Zero), presetAddress.Times.At, "moment");
        Assert.AreEqual(string.Empty, fixedAddress.Error, "no error for a fixed range");
        Assert.IsNull(fixedAddress.Times.Preset, "no preset");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), fixedAddress.Times.From, "start");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), fixedAddress.Times.To, "end");
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 30, 0, TimeSpan.Zero), fixedAddress.Times.At, "moment of the fixed range");
    }

    /// <summary>
    /// Any other key is ignored, <c>source</c>, <c>compare</c> and <c>metric</c> of the other views included, and with them an invalid value: the page has no source choice.
    /// </summary>
    /// <param name="query">The query string</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("?source=agent")]
    [DataRow("?source=a%20b&source=c")]
    [DataRow("?compare=2026-10-01T10:00&metric=system,cpu_percent&foo=bar")]
    public void ServicesAddressRefusesSourceAsUnknownKeyIgnored(string query)
    {
        // Arrange and act
        var address = ServicesAddress.Parse(query + "&range=1h");

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error for a key of another view");
        Assert.AreEqual(HistoryPreset.OneHour, address.Times.Preset, "the known key is read");
        Assert.AreEqual("?range=1h", address.ToQuery(), "an unknown key is not written");
    }

    /// <summary>
    /// Without a parameter, or with empty values, the address has no moment and its range is the last 30 days up to now; a preset or a fixed range gives its own.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="days">The days of the expected range up to now, or 0 for a fixed range</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("", 30)]
    [DataRow("?", 30)]
    [DataRow("?range=&from=&to=&at=", 30)]
    [DataRow("?range=7d", 7)]
    [DataRow("?range=1y", 365)]
    [DataRow("?from=2026-09-01T00:00&to=2026-09-02T00:00", 0)]
    public void ServicesAddressDefaultsToThirtyDays(string query, int days)
    {
        // Arrange
        var now = HistorySamples.Base;

        // Act
        var address = ServicesAddress.Parse(query);
        var range = address.Range(now);

        // Assert
        Assert.AreEqual(string.Empty, address.Error, "no error");
        Assert.IsNull(address.Times.At, "no moment");

        if (days == 0)
        {
            Assert.AreEqual(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), range.Start, "a fixed range keeps its start");
            Assert.AreEqual(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), range.End, "a fixed range keeps its end");

            return;
        }

        Assert.AreEqual(now.AddDays(-days), range.Start, "the range starts its span before now");
        Assert.AreEqual(now, range.End, "the range ends now");
    }

    /// <summary>
    /// The history view's errors and texts hold: a repeated parameter, an invalid value, a preset with times, one time and a start that is not before the end; the first error in the
    /// order range, from, to, at wins.
    /// </summary>
    /// <param name="query">The query string</param>
    /// <param name="expected">The error text</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("range=1h&range=7d", "The address gives the parameter range more than once.")]
    [DataRow("at=2026-10-01T10:00&AT=2026-10-01T10:00", "The address gives the parameter at more than once.")]
    [DataRow("from=2026-10-01T10:00&from=2026-10-01T11:00&to=2026-10-02T10:00", "The address gives the parameter from more than once.")]
    [DataRow("range=2h", "The address has an invalid value for the parameter range.")]
    [DataRow("from=2026-13-01T00:00&to=2026-10-02T00:00", "The address has an invalid value for the parameter from.")]
    [DataRow("from=2026-10-01T00:00&to=nonsense", "The address has an invalid value for the parameter to.")]
    [DataRow("at=x", "The address has an invalid value for the parameter at.")]
    [DataRow("at=x&from=y&range=2h", "The address has an invalid value for the parameter range.")]
    [DataRow("range=1h&from=2026-10-01T10:00&to=2026-10-02T10:00", BothKinds)]
    [DataRow("range=30d&to=2026-10-02T10:00", BothKinds)]
    [DataRow("from=2026-10-01T10:00", NeedsBoth)]
    [DataRow("to=2026-10-02T10:00", NeedsBoth)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-01T10:00", Reversed)]
    [DataRow("from=2026-10-02T10:00&to=2026-10-02T10:00", Reversed)]
    public void ServicesAddressReportsHistoryErrors(string query, string expected)
    {
        // Arrange and act
        var address = ServicesAddress.Parse(query);

        // Assert
        Assert.AreEqual(expected, address.Error, "the error text of the history view");
    }

    /// <summary>
    /// The parameters are written in the order range, start, end, moment and exactly in that form; an address without parameters is empty, and what is written reads back.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ServicesAddressToQueryWritesTheParametersInOrder()
    {
        // Arrange
        var preset = ServicesAddress.Parse("at=2026-10-01T10:00:30&range=7d");
        var fixedRange = ServicesAddress.Parse("at=2026-10-01T10:00:30&to=2026-10-02T11:30&from=2026-10-01T10:00");
        var bare = ServicesAddress.Parse(string.Empty);

        // Act
        var presetQuery = preset.ToQuery();
        var fixedQuery = fixedRange.ToQuery();
        var back = ServicesAddress.Parse(fixedQuery);

        // Assert
        Assert.AreEqual("?range=7d&at=2026-10-01T10:00:30", presetQuery, "preset address");
        Assert.AreEqual("?from=2026-10-01T10:00&to=2026-10-02T11:30&at=2026-10-01T10:00:30", fixedQuery, "fixed range address");
        Assert.AreEqual(string.Empty, bare.ToQuery(), "an address without parameters is empty");
        Assert.AreEqual(string.Empty, back.Error, "the written address is valid");
        Assert.AreEqual(fixedRange.Times.From, back.Times.From, "the start reads back");
        Assert.AreEqual(fixedRange.Times.At, back.Times.At, "the moment reads back");
    }

    /// <summary>
    /// An address that holds an error, a broken range or a time outside the storable range writes nothing: ToQuery throws with the text of the error.
    /// </summary>
    /// <param name="scenario">The scenario</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("error")]
    [DataRow("reversed range")]
    [DataRow("preset with times")]
    [DataRow("start without end")]
    [DataRow("moment outside the storable range")]
    [DataRow("start outside the storable range")]
    public void ServicesAddressWritesCheckedValuesOnly(string scenario)
    {
        // Arrange
        var address = new ServicesAddress();
        var at = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

        switch (scenario)
        {
            case "error":
                {
                    address.Error = "The address has an invalid value for the parameter range.";
                }
                break;

            case "reversed range":
                {
                    address.Times.From = at;
                    address.Times.To = at;
                }
                break;

            case "preset with times":
                {
                    address.Times.Preset = HistoryPreset.OneDay;
                    address.Times.From = at;
                    address.Times.To = at.AddDays(1);
                }
                break;

            case "start without end":
                {
                    address.Times.From = at;
                }
                break;

            case "moment outside the storable range":
                {
                    address.Times.At = DateTimeOffset.MaxValue;
                }
                break;

            default:
                {
                    address.Times.From = DateTimeOffset.MinValue;
                    address.Times.To = at;
                }
                break;
        }

        // Act and assert
        Assert.ThrowsExactly<InvalidOperationException>(() => address.ToQuery(), "an address that is not valid is not written");
    }

    /// <summary>
    /// A copy holds the same values and no change of the copy reaches the original; the same range ignores the moment and the error and compares the preset, the start and the end.
    /// </summary>
    [TestMethod]
    [TestCategory("AC1")]
    public void ServicesAddressCopiesAndComparesTheRange()
    {
        // Arrange
        var address = ServicesAddress.Parse("range=7d&at=2026-10-01T10:00:30");
        var otherMoment = ServicesAddress.Parse("range=7d&at=2026-10-01T11:00:30");
        var otherPreset = ServicesAddress.Parse("range=24h&at=2026-10-01T10:00:30");
        var fixedRange = ServicesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T11:30");
        var otherFixed = ServicesAddress.Parse("from=2026-10-01T10:00&to=2026-10-02T11:31");

        // Act
        var copy = address.Copy();

        copy.Times.At = null;
        copy.Error = "changed";

        // Assert
        Assert.IsNotNull(address.Times.At, "the moment of the original stays");
        Assert.AreEqual(string.Empty, address.Error, "the error of the original stays");
        Assert.AreEqual(HistoryPreset.SevenDays, copy.Times.Preset, "the copy has the preset");
        Assert.IsTrue(address.SameRange(otherMoment), "another moment is the same range");
        Assert.IsTrue(address.SameRange(copy), "a copy is the same range");
        Assert.IsFalse(address.SameRange(otherPreset), "another preset is another range");
        Assert.IsFalse(address.SameRange(fixedRange), "a fixed range is another range than a preset");
        Assert.IsFalse(fixedRange.SameRange(otherFixed), "another end is another range");
        Assert.IsTrue(fixedRange.SameRange(fixedRange.Copy()), "a copy of a fixed range is the same range");
    }

    #endregion // Methods
}