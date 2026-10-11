namespace Vandox.Core.LogParsing;

/// <summary>
/// The previous value of a vmstat counter.
/// </summary>
/// <param name="At">When the value was captured</param>
/// <param name="Value">The value</param>
internal sealed record LegacyCounter(DateTimeOffset At, ulong Value);