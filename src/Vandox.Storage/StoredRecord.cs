using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// A record as stored, with its storage context.
/// </summary>
public sealed class StoredRecord
{
    #region Properties

    /// <summary>
    /// Gets or sets the ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Gets or sets the record.
    /// </summary>
    public DataRecord Record { get; set; } = new();

    /// <summary>
    /// Gets or sets the agent ID; empty when the record has none.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the boot ID; empty when unknown.
    /// </summary>
    public string BootId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clock offset estimate in nanoseconds, if known.
    /// </summary>
    public long? ClockOffsetNs { get; set; }

    /// <summary>
    /// Gets or sets the instant the record was received.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; set; }

    #endregion // Properties
}