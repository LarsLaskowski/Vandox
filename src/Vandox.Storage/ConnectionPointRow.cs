namespace Vandox.Storage;

/// <summary>
/// The grouped values of one state or program in one step of a connection read.
/// </summary>
/// <param name="Name">The name</param>
/// <param name="Index">The step index</param>
/// <param name="Sum">The clamped sum of the values</param>
/// <param name="Seen">The number of snapshots that name the item</param>
/// <param name="Minimum">The smallest value</param>
/// <param name="Maximum">The largest value</param>
internal sealed record ConnectionPointRow(string Name, long Index, double Sum, long Seen, double Minimum, double Maximum);