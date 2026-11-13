namespace Vandox.Storage;

/// <summary>
/// A rollup bucket found by the removal walk.
/// </summary>
/// <param name="Source">The source</param>
/// <param name="Name">The metric name</param>
/// <param name="Labels">The labels text</param>
/// <param name="Bucket">The start of the bucket in nanoseconds</param>
internal readonly record struct RetentionBucketRow(string Source, string Name, string Labels, long Bucket);