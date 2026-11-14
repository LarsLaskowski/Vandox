namespace Vandox.Storage;

/// <summary>
/// A removable record found by the removal walk.
/// </summary>
/// <param name="Id">The record ID</param>
/// <param name="At">The capture time in nanoseconds</param>
internal readonly record struct RetentionRecordRow(long Id, long At);