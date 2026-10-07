using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="DataRecord"/>
/// </summary>
[TestClass]
public class DataRecordTests
{
    #region Constants

    private static readonly DateTimeOffset _captured = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A valid agent record passes and reports the kind of its payload.
    /// </summary>
    [TestMethod]
    public void DataRecordValidateValidAgentRecordReturnsNull()
    {
        // Arrange
        var record = Valid();

        // Act
        var error = record.Validate();

        // Assert
        Assert.IsNull(error, "valid record");
        Assert.AreEqual("metric", record.Kind, "kind");
    }

    /// <summary>
    /// A record without a payload has no kind and is invalid.
    /// </summary>
    [TestMethod]
    public void DataRecordValidateWithoutPayloadIsRequired()
    {
        // Arrange
        var record = Valid();

        record.Data = null;

        // Act
        var error = record.Validate();

        // Assert
        Assert.AreEqual("data", error?.Field, "field");
        Assert.AreEqual("required", error?.Reason, "reason");
        Assert.AreEqual(string.Empty, record.Kind, "kind of nothing");
    }

    /// <summary>
    /// A payload error is reported below "data".
    /// </summary>
    [TestMethod]
    public void DataRecordValidateBadPayloadIsPrefixedWithData()
    {
        // Arrange
        var record = Valid();

        record.Data = new MetricPoint
                      {
                          Name = string.Empty
                      };

        // Act
        var error = record.Validate();

        // Assert
        Assert.AreEqual("data.name", error?.Field, "field");
    }

    /// <summary>
    /// Metadata rules: origin, source, time and sequence number.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="source">The source</param>
    /// <param name="seq">The sequence number</param>
    /// <param name="offsetHours">The offset of the capture time</param>
    /// <param name="field">The expected field</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("x", "host", 1UL, 0, "origin", "unknown origin")]
    [DataRow("agent", "", 1UL, 0, "source", "required")]
    [DataRow("agent", "host", 1UL, 2, "captured_at", "must be UTC")]
    [DataRow("agent", "host", 0UL, 0, "seq", "must be greater than 0 for origin agent")]
    [DataRow("import", "host", 1UL, 0, "seq", "must be 0 unless origin is agent")]
    [DataRow("backend", "host", 0UL, 0, "", "")]
    public void DataRecordValidateChecksMetadata(string origin, string source, ulong seq, int offsetHours, string field, string reason)
    {
        // Arrange
        var record = Valid();

        record.Origin = origin;
        record.Source = source;
        record.Seq = seq;
        record.CapturedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(offsetHours));

        // Act
        var error = record.Validate();

        // Assert
        Assert.AreEqual(field, error?.Field ?? string.Empty, "field");
        Assert.AreEqual(reason, error?.Reason ?? string.Empty, "reason");
    }

    /// <summary>
    /// The agent never reports the gap causes only the backend can find out.
    /// </summary>
    /// <param name="origin">The origin</param>
    /// <param name="cause">The cause</param>
    /// <param name="expectedReason">The expected reason; empty when valid</param>
    [TestMethod]
    [DataRow("agent", "no_data", "not allowed for origin agent")]
    [DataRow("agent", "sequence_missing", "not allowed for origin agent")]
    [DataRow("agent", "unknown", "")]
    [DataRow("backend", "no_data", "")]
    public void DataRecordValidateGapCauseDependsOnOrigin(string origin, string cause, string expectedReason)
    {
        // Arrange
        var record = Valid();

        record.Origin = origin;
        record.Seq = origin == "agent" ? 1UL : 0UL;
        record.Data = new Gap
                      {
                          From = _captured,
                          To = _captured.AddMinutes(1),
                          Cause = cause,
                          FirstSeq = 1,
                          LastSeq = 2
                      };

        // Act
        var error = record.Validate();

        // Assert
        Assert.AreEqual(expectedReason, error?.Reason ?? string.Empty, "reason");
    }

    /// <summary>
    /// Creates a valid agent record.
    /// </summary>
    /// <returns>The record</returns>
    private static DataRecord Valid()
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Agent,
                   Source = "host",
                   Seq = 1,
                   CapturedAt = _captured,
                   Data = new MetricPoint
                          {
                              Name = "cpu",
                              Value = 1
                          }
               };
    }

    #endregion // Methods
}