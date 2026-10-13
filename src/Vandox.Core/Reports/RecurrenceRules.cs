namespace Vandox.Core.Reports;

#pragma warning disable RH2003, S2325

/// <summary>
/// The statistics behind the recurrence rules.
/// </summary>
public static class RecurrenceRules
{
    #region Methods

    /// <summary>
    /// Computes the probability of at least the given successes in the given trials.
    /// </summary>
    /// <param name="trials">The number of trials</param>
    /// <param name="successes">The number of successes</param>
    /// <param name="probability">The probability of one success</param>
    /// <returns>The upper tail of the binomial distribution</returns>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or the probability is outside 0 to 1</exception>
    public static double BinomialTail(long trials, long successes, double probability)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Decides a rule from the counts of the outages and of the observed hours.
    /// </summary>
    /// <param name="matching">The outages that match</param>
    /// <param name="known">The outages with a known answer</param>
    /// <param name="baseMatching">The observed hours that match</param>
    /// <param name="baseKnown">The observed hours</param>
    /// <param name="level">The significance level</param>
    /// <returns>The result</returns>
    public static PatternResult Evaluate(long matching, long known, long baseMatching, long baseKnown, double level)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}