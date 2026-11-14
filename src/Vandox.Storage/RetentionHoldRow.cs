namespace Vandox.Storage;

/// <summary>
/// A stored hold or an incident as the removal walk reads it.
/// </summary>
/// <param name="Id">The row ID</param>
/// <param name="Start">The start in nanoseconds</param>
/// <param name="End">The end in nanoseconds; 0 for an incident, which the walk reads without it</param>
internal readonly record struct RetentionHoldRow(long Id, long Start, long End);