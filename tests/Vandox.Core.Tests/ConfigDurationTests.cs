using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ConfigDuration"/>
/// </summary>
[TestClass]
public class ConfigDurationTests
{
    #region Methods

    /// <summary>
    /// One to four ASCII digits and the unit m or h give the duration; leading zeros are allowed.
    /// </summary>
    /// <param name="text">The text</param>
    /// <param name="minutes">The expected number of minutes</param>
    [TestMethod]
    [DataRow("1m", 1)]
    [DataRow("30m", 30)]
    [DataRow("0090m", 90)]
    [DataRow("1h", 60)]
    [DataRow("24h", 1440)]
    [DataRow("9999h", 599940)]
    public void ConfigDurationAcceptsMinutesAndHours(string text, int minutes)
    {
        // Act
        var parsed = ConfigDuration.TryParse(text, out var value);

        // Assert
        Assert.IsTrue(parsed, $"{text} is accepted");
        Assert.AreEqual(TimeSpan.FromMinutes(minutes), value, $"duration of {text}");
    }

    /// <summary>
    /// Every other form is refused: no sign, fraction, white space, other unit, upper case unit, missing unit or digit, or more than four digits.
    /// </summary>
    /// <param name="text">The text</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("m")]
    [DataRow("60")]
    [DataRow("1")]
    [DataRow("1.5h")]
    [DataRow("+1h")]
    [DataRow("-1h")]
    [DataRow("1H")]
    [DataRow("1M")]
    [DataRow("1 h")]
    [DataRow(" 1h")]
    [DataRow("1h ")]
    [DataRow("1h30m")]
    [DataRow("1d")]
    [DataRow("1s")]
    [DataRow("12345m")]
    [DataRow("1hh")]
    [DataRow("h1")]
    [DataRow("0x1h")]
    [DataRow("1_0m")]
    public void ConfigDurationRefusesOtherForms(string text)
    {
        // Act
        var parsed = ConfigDuration.TryParse(text, out var value);

        // Assert
        Assert.IsFalse(parsed, $"'{text}' is refused");
        Assert.AreEqual(TimeSpan.Zero, value, "no duration is returned for a refused text");
    }

    /// <summary>
    /// Digits of other scripts are not digits of a duration.
    /// </summary>
    /// <param name="digit">The code point of the digit one in another script</param>
    [TestMethod]
    [DataRow(0x0661)]
    [DataRow(0xFF11)]
    public void ConfigDurationRefusesNonAsciiDigits(int digit)
    {
        // Arrange
        var input = char.ConvertFromUtf32(digit) + "h";

        Assert.AreEqual(2, input.Length, "the input is one digit and the unit");
        Assert.IsGreaterThan(0x7F, (int)input[0], "the digit is outside ASCII");

        // Act
        var parsed = ConfigDuration.TryParse(input, out var value);

        // Assert
        Assert.IsFalse(parsed, "a digit of another script is refused");
        Assert.AreEqual(TimeSpan.Zero, value, "no duration is returned");
    }

    #endregion // Methods
}