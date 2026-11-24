namespace Vandox.Storage;

/// <summary>
/// What the connection reads share: the tier, the parameters, the totals per step and the completeness.
/// </summary>
/// <param name="Hourly">Whether the hourly tier is read</param>
/// <param name="Parameters">The parameters of the statements</param>
/// <param name="Totals">The totals per step, or <c>null</c> when the range holds no total</param>
/// <param name="EarliestUnfolded">The earliest capture time of the source's unfolded snapshots</param>
/// <param name="StartsBeforeBuckets">Whether the range starts before the oldest bucket of the tier</param>
internal sealed record ConnectionReadContext(bool Hourly, Dictionary<string, object> Parameters, ConnectionStepTotals? Totals, DateTimeOffset? EarliestUnfolded, bool StartsBeforeBuckets);