namespace Vandox.Storage;

/// <summary>
/// Names the bucket of a source.
/// </summary>
/// <param name="Source">The source</param>
/// <param name="Bucket">The start of the bucket in nanoseconds</param>
internal readonly record struct SourceBucketKey(string Source, long Bucket);