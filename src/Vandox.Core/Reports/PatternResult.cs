namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The result of one recurrence rule.
/// </summary>
public sealed class PatternResult
{
    #region Properties

    /// <summary>
    /// Gets the outcome.
    /// </summary>
    public PatternOutcome Outcome { get; init; }

    /// <summary>
    /// Gets why the rule is unknown; <see cref="UnknownReason.None"/> otherwise.
    /// </summary>
    public UnknownReason Reason { get; init; }

    /// <summary>
    /// Gets the outages that match.
    /// </summary>
    public long Matching { get; init; }

    /// <summary>
    /// Gets the outages with a known answer.
    /// </summary>
    public long Known { get; init; }

    /// <summary>
    /// Gets the observed hours that match.
    /// </summary>
    public long BaseMatching { get; init; }

    /// <summary>
    /// Gets the observed hours.
    /// </summary>
    public long BaseKnown { get; init; }

    /// <summary>
    /// Gets the share of matching outages; <c>null</c> when no outage is known.
    /// </summary>
    public double? Share => throw new NotImplementedException();

    /// <summary>
    /// Gets the share of matching observed hours; <c>null</c> when no hour is observed.
    /// </summary>
    public double? BaseShare => throw new NotImplementedException();

    #endregion // Properties
}