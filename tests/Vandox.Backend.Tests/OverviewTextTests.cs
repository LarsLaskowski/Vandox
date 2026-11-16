using Vandox.Backend.Overview;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="OverviewText"/>
/// </summary>
[TestClass]
public class OverviewTextTests
{
    #region Methods

    /// <summary>
    /// A size below 1 KiB is written in whole bytes, a larger one in the largest binary unit it reaches, at most TiB, with one decimal.
    /// </summary>
    /// <param name="bytes">The size in bytes</param>
    /// <param name="expected">The expected text</param>
    [TestMethod]
    [TestCategory("AC3")]
    [TestCategory("AC5")]
    [DataRow(0d, "0 B")]
    [DataRow(1023d, "1023 B")]
    [DataRow(1024d, "1.0 KiB")]
    [DataRow(1536d, "1.5 KiB")]
    [DataRow(1048576d, "1.0 MiB")]
    [DataRow(3435973837d, "3.2 GiB")]
    [DataRow(8589934592d, "8.0 GiB")]
    [DataRow(1099511627776d, "1.0 TiB")]
    [DataRow(1125899906842624d, "1024.0 TiB")]
    public void SizeWritesBinaryUnitsWithOneDecimal(double bytes, string expected)
    {
        // Act
        var text = OverviewText.Size(bytes);

        // Assert
        Assert.AreEqual(expected, text, $"the size of {bytes} bytes");
    }

    /// <summary>
    /// A share is written in percent with one decimal.
    /// </summary>
    /// <param name="part">The part</param>
    /// <param name="total">The total</param>
    /// <param name="expected">The expected text</param>
    [TestMethod]
    [TestCategory("AC5")]
    [DataRow(1d, 4d, "25.0 %")]
    [DataRow(1d, 3d, "33.3 %")]
    [DataRow(2d, 3d, "66.7 %")]
    [DataRow(0d, 8d, "0.0 %")]
    [DataRow(8d, 8d, "100.0 %")]
    public void ShareWritesPercentWithOneDecimal(double part, double total, string expected)
    {
        // Act
        var text = OverviewText.Share(part, total);

        // Assert
        Assert.AreEqual(expected, text, $"the share of {part} in {total}");
    }

    /// <summary>
    /// An age is written as whole seconds below a minute, whole minutes below an hour, hours and minutes below a day, days and hours above.
    /// </summary>
    /// <param name="seconds">The age in seconds</param>
    /// <param name="expected">The expected text</param>
    [TestMethod]
    [TestCategory("AC1")]
    [DataRow(0d, "0 s")]
    [DataRow(45d, "45 s")]
    [DataRow(45.9d, "45 s")]
    [DataRow(59d, "59 s")]
    [DataRow(60d, "1 min")]
    [DataRow(720d, "12 min")]
    [DataRow(779d, "12 min")]
    [DataRow(3599d, "59 min")]
    [DataRow(3600d, "1 h 0 min")]
    [DataRow(11100d, "3 h 5 min")]
    [DataRow(86399d, "23 h 59 min")]
    [DataRow(86400d, "1 d 0 h")]
    [DataRow(187200d, "2 d 4 h")]
    public void AgeWritesTheUnitOfItsRange(double seconds, string expected)
    {
        // Act
        var text = OverviewText.Age(TimeSpan.FromSeconds(seconds));

        // Assert
        Assert.AreEqual(expected, text, $"the age of {seconds} seconds");
    }

    #endregion // Methods
}