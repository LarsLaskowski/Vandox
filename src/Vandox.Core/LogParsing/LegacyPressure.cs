namespace Vandox.Core.LogParsing;

/// <summary>
/// One pressure stall line of a resource.
/// </summary>
/// <param name="Resource">The resource: memory, cpu or io</param>
/// <param name="Kind">The kind: some or full</param>
/// <param name="Avg10">The average over 10 seconds</param>
/// <param name="Avg60">The average over 60 seconds</param>
/// <param name="Avg300">The average over 300 seconds</param>
/// <param name="Total">The total stall time in microseconds</param>
internal sealed record LegacyPressure(string Resource, string Kind, double Avg10, double Avg60, double Avg300, ulong Total);