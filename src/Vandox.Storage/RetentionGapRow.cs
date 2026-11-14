namespace Vandox.Storage;

/// <summary>
/// A gap row found by the removal walk.
/// </summary>
/// <param name="Id">The row ID</param>
/// <param name="Kind">The gap kind</param>
/// <param name="State">The state as it reads</param>
/// <param name="From">The start of the span in nanoseconds</param>
/// <param name="To">The end of the span in nanoseconds</param>
internal readonly record struct RetentionGapRow(long Id, string Kind, string State, long From, long To);