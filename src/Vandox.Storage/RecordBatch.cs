using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// A set of records written in one transaction, with the context shared by all of them.
/// </summary>
public sealed class RecordBatch
{
    #region Properties

    /// <summary>
    /// Gets or sets the agent ID; required when a record has origin agent, empty otherwise.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the boot ID; empty when unknown, then stored as NULL.
    /// </summary>
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clock offset estimate in nanoseconds, if known.
    /// </summary>
    public long? ClockOffsetNs { get; set; }

    /// <summary>
    /// Gets or sets the instant the batch was received, in UTC.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// Gets or sets the records.
    /// </summary>
    public IReadOnlyList<DataRecord> Records { get; set; } = [];

    /// <summary>
    /// Gets or sets the import step: when set, every record has origin import, the agent ID is empty, and the step is
    /// applied in the same transaction.
    /// </summary>
    public ImportStep? Import { get; set; }

    #endregion // Properties
}