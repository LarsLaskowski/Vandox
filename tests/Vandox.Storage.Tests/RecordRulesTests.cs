using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="RecordRules"/>
/// </summary>
[TestClass]
public class RecordRulesTests
{
    #region Constants

    private const string TooLarge = "data is larger than 16777216 bytes as JSON";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A payload whose JSON is exactly the limit is accepted.
    /// </summary>
    [TestMethod]
    public void RecordRulesCheckPayloadSizeAcceptsDataOfExactlyTheLimit()
    {
        // Arrange
        var record = GapOfJsonLength(StorageLimits.MaxPayloadBytes);

        // Act
        var actual = RecordRules.CheckPayloadSize(record);

        // Assert
        Assert.IsNull(actual, "exactly the limit");
    }

    /// <summary>
    /// A payload whose JSON is one byte above the limit is refused with the fixed text.
    /// </summary>
    [TestMethod]
    public void RecordRulesCheckPayloadSizeRefusesDataAboveTheLimit()
    {
        // Arrange
        var record = GapOfJsonLength(StorageLimits.MaxPayloadBytes + 1);

        // Act
        var actual = RecordRules.CheckPayloadSize(record);

        // Assert
        Assert.AreEqual(TooLarge, actual, "one byte above the limit");
    }

    /// <summary>
    /// Metrics and log lines keep no JSON and are not checked, whatever their size.
    /// </summary>
    /// <param name="typed">The typed record to check</param>
    [TestMethod]
    [DataRow("metric")]
    [DataRow("log")]
    public void RecordRulesCheckPayloadSizeIgnoresTypedRecords(string typed)
    {
        // Arrange
        var record = Samples.Metric(RecordOrigin.Backend, 0, 0, "cpu");

        if (typed == "metric")
        {
            ((MetricPoint)record.Data!).Name = new string('a', StorageLimits.MaxPayloadBytes + 1);
        }
        else
        {
            record = Samples.Log(RecordOrigin.Backend, 0, 0, new string('a', StorageLimits.MaxPayloadBytes + 1));
        }

        // Act
        var actual = RecordRules.CheckPayloadSize(record);

        // Assert
        Assert.IsNull(actual, "typed records are not checked");
    }

    /// <summary>
    /// Creates a gap record whose payload has exactly the given JSON length.
    /// </summary>
    /// <param name="length">The UTF-8 bytes of the JSON</param>
    /// <returns>The record</returns>
    private static DataRecord GapOfJsonLength(int length)
    {
        var record = Samples.Gap(0);
        var gap = (Gap)record.Data!;
        gap.Cause = string.Empty;

        var baseLength = JsonSerializer.SerializeToUtf8Bytes(gap, PayloadRegistry.Options).Length;

        gap.Cause = new string('a', length - baseLength);

        Assert.HasCount(length, JsonSerializer.SerializeToUtf8Bytes(gap, PayloadRegistry.Options), "length of the JSON");

        return record;
    }

    #endregion // Methods
}