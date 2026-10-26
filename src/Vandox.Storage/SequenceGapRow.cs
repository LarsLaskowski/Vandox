namespace Vandox.Storage;

/// <summary>
/// A sequence gap as the seeks of the timeline read it.
/// </summary>
/// <param name="Id">The ID</param>
/// <param name="First">The first missing number</param>
/// <param name="Last">The last missing number</param>
/// <param name="State">The state</param>
/// <param name="Cause">The cause</param>
/// <param name="FromAt">The start of the span in nanoseconds</param>
/// <param name="ToAt">The end of the span in nanoseconds</param>
/// <param name="DetectedAt">When the gap was detected in nanoseconds</param>
internal readonly record struct SequenceGapRow(long Id, long First, long Last, string State, string Cause, long FromAt, long ToAt, long DetectedAt);