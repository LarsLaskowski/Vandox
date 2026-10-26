namespace Vandox.Storage;

/// <summary>
/// A time gap as the seek of the timeline reads it.
/// </summary>
/// <param name="Id">The ID</param>
/// <param name="From">The start in nanoseconds</param>
/// <param name="To">The end in nanoseconds</param>
/// <param name="FromSeq">The highest metric number at the start</param>
/// <param name="ToSeq">The lowest metric number at the end</param>
/// <param name="DetectedAt">When the gap was detected in nanoseconds</param>
internal readonly record struct TimeGapRow(long Id, long From, long To, long? FromSeq, long? ToSeq, long DetectedAt);