using Vandox.Backend.Ingest;
using Vandox.Core.Wire;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestStatus"/> and <see cref="IngestLimits"/>
/// </summary>
[TestClass]
public class IngestStatusTests
{
    #region Methods

    /// <summary>
    /// Every error class of the decoder has its status and its fixed reason.
    /// </summary>
    /// <param name="kind">The error class</param>
    /// <param name="status">The expected status</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow(WireErrorKind.Malformed, 400, "malformed")]
    [DataRow(WireErrorKind.LimitExceeded, 413, "limit exceeded")]
    [DataRow(WireErrorKind.UnsupportedVersion, 415, "format version")]
    [DataRow(WireErrorKind.UnknownKind, 422, "unknown kind")]
    [DataRow(WireErrorKind.Sequence, 422, "sequence")]
    [DataRow(WireErrorKind.EmptyBatch, 422, "empty batch")]
    [DataRow(WireErrorKind.Invalid, 422, "invalid")]
    public void IngestStatusMapsEveryErrorClass(WireErrorKind kind, int status, string reason)
    {
        // Act
        var actualStatus = IngestStatus.ForWireError(kind);
        var actualReason = IngestStatus.Reason(kind);

        // Assert
        Assert.AreEqual(status, actualStatus, $"status of {kind}");
        Assert.AreEqual(reason, actualReason, $"reason of {kind}");
    }

    /// <summary>
    /// Only the answers a batch refusal uses carry the refusal header; 415, 408, 503 and every 2xx do not.
    /// </summary>
    /// <param name="status">The status</param>
    /// <param name="expected">Whether the status is a batch refusal</param>
    [TestMethod]
    [DataRow(400, true)]
    [DataRow(413, true)]
    [DataRow(422, true)]
    [DataRow(401, false)]
    [DataRow(403, false)]
    [DataRow(408, false)]
    [DataRow(415, false)]
    [DataRow(503, false)]
    [DataRow(204, false)]
    public void IngestStatusIsRefusalOnlyForTheRefusingStatuses(int status, bool expected)
    {
        // Act
        var actual = IngestStatus.IsRefusal(status);

        // Assert
        Assert.AreEqual(expected, actual, $"status {status}");
    }

    /// <summary>
    /// The wire limits of the endpoint are the constants of the limits, and the budget is 30 seconds.
    /// </summary>
    [TestMethod]
    public void IngestLimitsWireCarriesTheConstants()
    {
        // Act
        var limits = IngestLimits.Wire();

        // Assert
        Assert.AreEqual(1 << 20, limits.MaxLineBytes, "line limit is 1 MiB");
        Assert.AreEqual(2L << 20, limits.MaxBatchBytes, "batch limit is 2 MiB");
        Assert.AreEqual(20000, limits.MaxRecords, "record limit");
        Assert.AreSequenceEqual(new[] { 2 << 20, 2, 16 }, new[] { IngestLimits.MaxBodyBytes, IngestLimits.Slots, IngestLimits.MaxConnections }, "body limit 2 MiB, two slots, sixteen connections");
        Assert.AreEqual(TimeSpan.FromSeconds(30), IngestLimits.RequestBudget, "budget");
    }

    #endregion // Methods
}