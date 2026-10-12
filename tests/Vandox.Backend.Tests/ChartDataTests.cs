using Vandox.Backend.Components.Charts;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ChartData"/>
/// </summary>
[TestClass]
public class ChartDataTests
{
    #region Fields

    private static readonly DateTimeOffset _start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// The data keeps its inputs, converts the times to Unix seconds and leaves a gap as <c>null</c>.
    /// </summary>
    [TestMethod]
    public void ChartDataConvertsTimesToUnixSecondsAndKeepsGaps()
    {
        // Arrange
        var times = new[] { _start, _start.AddMilliseconds(500), _start.AddSeconds(60) };
        var first = new ChartSeries("load", [1.5, null, 3.0]);
        var second = new ChartSeries("memory", [null, 20.0, 30.0]);

        // Act
        var data = new ChartData(times, [first, second]);
        var columns = data.ToColumns();

        // Assert
        Assert.AreSequenceEqual(times, data.Times.ToArray(), "times are kept");
        Assert.AreSequenceEqual(new[] { first, second }, data.Series.ToArray(), "series are kept");
        Assert.HasCount(3, columns, "one column for the times and one per series");
        Assert.AreSequenceEqual(new double?[] { 1791763200.0, 1791763200.5, 1791763260.0 }, columns[0], "times as Unix seconds");
        Assert.AreSequenceEqual(new double?[] { 1.5, null, 3.0 }, columns[1], "first series keeps its gap");
        Assert.AreSequenceEqual(new double?[] { null, 20.0, 30.0 }, columns[2], "second series keeps its gap");
    }

    /// <summary>
    /// Data that cannot be drawn is refused with an <see cref="ArgumentException"/>.
    /// </summary>
    /// <param name="kind">The kind of invalid data</param>
    [TestMethod]
    [DataRow("LengthMismatch")]
    [DataRow("Descending")]
    [DataRow("Equal")]
    [DataRow("NaN")]
    [DataRow("PositiveInfinity")]
    [DataRow("NegativeInfinity")]
    [DataRow("NoSeries")]
    public void ChartDataRefusesInvalidData(string kind)
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2) };
        IReadOnlyList<DateTimeOffset> usedTimes = kind switch
                                                  {
                                                      "Descending" => [_start, _start.AddSeconds(2), _start.AddSeconds(1)],
                                                      "Equal" => [_start, _start.AddSeconds(1), _start.AddSeconds(1)],
                                                      _ => times
                                                  };
        IReadOnlyList<ChartSeries> series = kind switch
                                            {
                                                "LengthMismatch" => [new ChartSeries("a", [1.0, 2.0])],
                                                "NaN" => [new ChartSeries("a", [1.0, double.NaN, 3.0])],
                                                "PositiveInfinity" => [new ChartSeries("a", [1.0, double.PositiveInfinity, 3.0])],
                                                "NegativeInfinity" => [new ChartSeries("a", [1.0, double.NegativeInfinity, 3.0])],
                                                "NoSeries" => [],
                                                _ => [new ChartSeries("a", [1.0, 2.0, 3.0])]
                                            };

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => new ChartData(usedTimes, series), $"invalid data: {kind}");

        // Assert
        Assert.IsNotNull(exception, "the refusal is an ArgumentException");
    }

    /// <summary>
    /// A null argument or label is refused with an <see cref="ArgumentNullException"/>.
    /// </summary>
    [TestMethod]
    public void ChartDataRefusesNullArguments()
    {
        // Arrange
        var series = new[] { new ChartSeries("a", [1.0]) };

        // Act
        var nullTimes = Assert.ThrowsExactly<ArgumentNullException>(() => new ChartData(null!, series), "null times");
        var nullSeries = Assert.ThrowsExactly<ArgumentNullException>(() => new ChartData([_start], null!), "null series");
        var nullLabel = Assert.ThrowsExactly<ArgumentNullException>(() => new ChartSeries(null!, [1.0]), "null label");
        var nullValues = Assert.ThrowsExactly<ArgumentNullException>(() => new ChartSeries("a", null!), "null values");

        // Assert
        Assert.IsNotNull(nullTimes, "times");
        Assert.IsNotNull(nullSeries, "series");
        Assert.IsNotNull(nullLabel, "label");
        Assert.IsNotNull(nullValues, "values");
    }

    #endregion // Methods
}