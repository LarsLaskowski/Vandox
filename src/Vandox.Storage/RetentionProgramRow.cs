namespace Vandox.Storage;

/// <summary>
/// A program rollup row found by the removal walk.
/// </summary>
/// <param name="Source">The source</param>
/// <param name="Bucket">The start of the bucket in nanoseconds</param>
/// <param name="Program">The program name</param>
internal readonly record struct RetentionProgramRow(string Source, long Bucket, string Program);