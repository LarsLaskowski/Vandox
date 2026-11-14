namespace Vandox.Storage;

/// <summary>
/// Names a bucket of a series.
/// </summary>
/// <param name="Source">The source</param>
/// <param name="Name">The metric name</param>
/// <param name="Labels">The labels text, empty for none</param>
/// <param name="Bucket">The start of the bucket in nanoseconds</param>
internal readonly record struct RollupBucketKey(string Source, string Name, string Labels, long Bucket);