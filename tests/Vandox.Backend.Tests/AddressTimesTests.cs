using Microsoft.Extensions.Primitives;

using Vandox.Backend.History;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="AddressTimes"/>
/// </summary>
[TestClass]
public class AddressTimesTests
{
    #region Methods

    /// <summary>
    /// A parameter whose value is null counts as an empty value and sets nothing, and the comparison of ranges refuses no range.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void AddressTimesReadValuesTreatsANullValueAsEmpty()
    {
        // Arrange
        var times = new AddressTimes();
        var values = new Dictionary<string, StringValues>
                     {
                         ["range"] = new StringValues([null])
                     };

        // Act
        var error = times.ReadValues(values);

        // Assert
        Assert.AreEqual(string.Empty, error, "an empty value is no error");
        Assert.IsNull(times.Preset, "the preset stays unset");
    }

    /// <summary>
    /// A name parameter that is absent, empty or null gives no error and no value, and a valid one gives its value.
    /// </summary>
    /// <param name="value">The value, <c>null</c> for an absent parameter</param>
    /// <param name="expected">The expected value</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow("agent", "agent")]
    [DataRow("a.b:c/d@e+f-g", "a.b:c/d@e+f-g")]
    public void AddressTimesReadNameReadsAValidOrEmptyName(string? value, string? expected)
    {
        // Arrange
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);

        if (value is not null)
        {
            values["Source"] = new StringValues(value);
        }

        // Act
        var (error, name) = AddressTimes.ReadName(values, "source");

        // Assert
        Assert.AreEqual(string.Empty, error, "no error");
        Assert.AreEqual(expected, name, "the value");
    }

    /// <summary>
    /// A name parameter given twice or with a value outside the name pattern is an error that names the parameter and never the value, and gives no value.
    /// </summary>
    /// <param name="first">The first value</param>
    /// <param name="second">The second value, <c>null</c> for none</param>
    /// <param name="expected">The expected error</param>
    [TestMethod]
    [TestCategory("AC7")]
    [DataRow("agent", "agent", "The address gives the parameter source more than once.")]
    [DataRow("a b", null, "The address has an invalid value for the parameter source.")]
    [DataRow("-a", null, "The address has an invalid value for the parameter source.")]
    [DataRow("agent\n", null, "The address has an invalid value for the parameter source.")]
    public void AddressTimesReadNameRefusesARepeatedOrInvalidName(string first, string? second, string expected)
    {
        // Arrange
        var values = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
                     {
                         ["source"] = second is null ? new StringValues(first) : new StringValues([first, second])
                     };

        // Act
        var (error, name) = AddressTimes.ReadName(values, "source");

        // Assert
        Assert.AreEqual(expected, error, "the error names the parameter and not its value");
        Assert.IsNull(name, "no value is kept");
    }

    /// <summary>
    /// A name parameter with a null element counts as an empty value.
    /// </summary>
    [TestMethod]
    [TestCategory("AC7")]
    public void AddressTimesReadNameTreatsANullValueAsEmpty()
    {
        // Arrange
        var values = new Dictionary<string, StringValues>
                     {
                         ["source"] = new StringValues([null])
                     };

        // Act
        var (error, name) = AddressTimes.ReadName(values, "source");

        // Assert
        Assert.AreEqual(string.Empty, error, "no error");
        Assert.IsNull(name, "no value");
    }

    /// <summary>
    /// Comparing with no range is refused.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void AddressTimesSameRangeRefusesNoRange()
    {
        // Arrange
        var times = new AddressTimes();

        // Act
        var thrown = Assert.Throws<ArgumentNullException>(() => times.SameRange(null!), "no range");

        // Assert
        Assert.AreEqual("other", thrown.ParamName, "the parameter of the refusal");
    }

    /// <summary>
    /// A range without a preset or times is the span of the fallback preset up to now, a preset of the address wins over the fallback, and a fixed range keeps its times.
    /// </summary>
    /// <param name="preset">The preset of the address, or an empty text for none</param>
    /// <param name="fallback">The fallback preset</param>
    /// <param name="days">The days of the expected range up to now, or 0 for the fixed range</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow("", HistoryPreset.ThirtyDays, 30)]
    [DataRow("", HistoryPreset.OneYear, 365)]
    [DataRow("", HistoryPreset.SevenDays, 7)]
    [DataRow("7d", HistoryPreset.ThirtyDays, 7)]
    [DataRow("1y", HistoryPreset.OneDay, 365)]
    [DataRow("fixed", HistoryPreset.ThirtyDays, 0)]
    public void AddressTimesRangeUsesFallbackPreset(string preset, HistoryPreset fallback, int days)
    {
        // Arrange
        var now = new DateTimeOffset(2026, 10, 5, 12, 34, 56, TimeSpan.Zero);
        var times = new AddressTimes();

        if (preset == "fixed")
        {
            times.From = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
            times.To = new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero);
        }
        else if (preset.Length > 0)
        {
            times.Preset = preset == "7d" ? HistoryPreset.SevenDays : HistoryPreset.OneYear;
        }

        // Act
        var range = times.Range(now, fallback);

        // Assert
        if (days == 0)
        {
            Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), range.Start, "a fixed range keeps its start");
            Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 30, 15, TimeSpan.Zero), range.End, "a fixed range keeps its end");

            return;
        }

        Assert.AreEqual(now.AddDays(-days), range.Start, "the range starts the days of its preset before now");
        Assert.AreEqual(now, range.End, "the range ends now");
    }

    #endregion // Methods
}