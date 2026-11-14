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

    private static readonly int[] _bridgedIndexes = [1, 3];

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
    /// A stacked chart refuses a series that has no value at a point and says which one, a line chart still accepts a gap, and the flag tells the two apart.
    /// </summary>
    [TestMethod]
    public void ChartDataStackedRefusesMissingValues()
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2) };
        var complete = new ChartSeries("complete", [1.0, 2.0, 3.0]);
        var gap = new ChartSeries("with gap", [1.0, null, 3.0]);

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => new ChartData(times, [complete, gap], true), "a stacked series with a gap");
        var stacked = new ChartData(times, [complete], true);
        var lines = new ChartData(times, [gap], false);
        var plain = new ChartData(times, [gap]);

        // Assert
        Assert.StartsWith("The stacked series 'with gap' has no value at point 1.", exception.Message, "the message names the series and the point");
        Assert.IsTrue(stacked.Stacked, "a complete stacked chart is accepted");
        Assert.IsFalse(lines.Stacked, "a line chart with a gap is accepted");
        Assert.IsFalse(plain.Stacked, "a chart made without the flag is not stacked");
    }

    /// <summary>
    /// A series with a bridged point that has a value is refused, and the message names the series and the point; a bridged point without a value is accepted.
    /// </summary>
    [TestMethod]
    [TestCategory("AC11")]
    public void ChartDataConstructorRefusesBridgedPointWithValue()
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2) };
        var withValue = new ChartSeries("load", [1.0, 2.0, null], [false, true, false]);
        var withoutValue = new ChartSeries("load", [1.0, null, 3.0], [false, true, false]);

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => new ChartData(times, [withValue]), "a bridged point with a value");
        var accepted = new ChartData(times, [withoutValue]);

        // Assert
        Assert.StartsWith("The series 'load' has a value at bridged point 1.", exception.Message, "the message names the series and the point");
        Assert.HasCount(1, accepted.Series, "a bridged point without a value is accepted");
    }

    /// <summary>
    /// A bridge list whose length is neither zero nor the number of points is refused with the count of flags and points in the message.
    /// </summary>
    /// <param name="flags">The number of bridge flags</param>
    [TestMethod]
    [TestCategory("AC11")]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public void ChartDataConstructorRefusesBridgeCountMismatch(int flags)
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2) };
        var series = new ChartSeries("load", [1.0, null, 3.0], [.. new bool[flags]]);

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => new ChartData(times, [series]), "a wrong count of bridge flags");
        var none = new ChartData(times, [new ChartSeries("load", [1.0, null, 3.0], [])]);

        // Assert
        Assert.StartsWith($"The series 'load' has {flags} bridge flags for 3 points in time.", exception.Message, "the message names the series and both counts");
        Assert.HasCount(1, none.Series, "no bridge flags at all is accepted");
    }

    /// <summary>
    /// A stacked series that bridges a point is refused, and the message names the series.
    /// </summary>
    [TestMethod]
    [TestCategory("AC11")]
    public void ChartDataConstructorRefusesBridgedStackedSeries()
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2) };
        var series = new ChartSeries("memory", [1.0, 2.0, 3.0], [false, true, false]);

        // Act
        var exception = Assert.ThrowsExactly<ArgumentException>(() => new ChartData(times, [series], true), "a bridged stacked series");

        // Assert
        Assert.StartsWith("The stacked series 'memory' cannot bridge points.", exception.Message, "the message names the series");
    }

    /// <summary>
    /// The bridges hold per series the indexes of the bridged points, in ascending order, and an empty list for a series without any.
    /// </summary>
    [TestMethod]
    [TestCategory("AC11")]
    public void ChartDataToBridgesListsBridgedIndexesPerSeries()
    {
        // Arrange
        var times = new[] { _start, _start.AddSeconds(1), _start.AddSeconds(2), _start.AddSeconds(3) };
        var bridged = new ChartSeries("a", [1.0, null, 3.0, null], [false, true, false, true]);
        var plain = new ChartSeries("b", [1.0, 2.0, null, 4.0]);
        var unbridged = new ChartSeries("c", [1.0, 2.0, 3.0, 4.0], [false, false, false, false]);
        var data = new ChartData(times, [bridged, plain, unbridged]);

        // Act
        var bridges = data.ToBridges();

        // Assert
        Assert.HasCount(3, bridges, "one list per series");
        Assert.AreSequenceEqual(_bridgedIndexes, bridges[0], "the bridged indexes of the first series");
        Assert.IsEmpty(bridges[1], "a series without flags has no bridged index");
        Assert.IsEmpty(bridges[2], "a series with only false flags has no bridged index");
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