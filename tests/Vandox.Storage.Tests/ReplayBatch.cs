namespace Vandox.Storage.Tests;

/// <summary>
/// A batch an <see cref="AgentReplay"/> sends, with the stream it was taken from.
/// </summary>
/// <param name="Batch">The batch</param>
/// <param name="Backfill"><see langword="true"/> when the batch comes from the backfill stream</param>
/// <param name="Live"><see langword="true"/> when the batch comes from the live stream</param>
internal sealed record ReplayBatch(RecordBatch Batch, bool Backfill, bool Live);