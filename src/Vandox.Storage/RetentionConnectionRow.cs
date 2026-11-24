namespace Vandox.Storage;

/// <summary>
/// A connection rollup row found by the removal walk.
/// </summary>
/// <param name="Source">The source</param>
/// <param name="Bucket">The start of the bucket in nanoseconds</param>
/// <param name="Kind">The kind of the row: 0 a state, 1 a program, 2 an address</param>
/// <param name="Name">The name of the state, program or address</param>
internal readonly record struct RetentionConnectionRow(string Source, long Bucket, long Kind, string Name);