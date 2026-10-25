namespace Vandox.Backend.Tests;

/// <summary>
/// One row of the records table as the ingest tests read it back.
/// </summary>
/// <param name="Seq">The sequence number</param>
/// <param name="Origin">The origin</param>
/// <param name="AgentId">The agent ID</param>
/// <param name="BootId">The boot ID</param>
/// <param name="ClockOffsetNs">The clock offset</param>
/// <param name="ReceivedAt">The receive time in nanoseconds since the Unix epoch</param>
/// <param name="Kind">The kind</param>
/// <param name="Data">The stored payload as JSON text</param>
internal sealed record StoredRow(long Seq, string Origin, string? AgentId, string? BootId, long? ClockOffsetNs, long ReceivedAt, string Kind, string? Data);