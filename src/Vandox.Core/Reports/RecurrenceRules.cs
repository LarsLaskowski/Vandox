namespace Vandox.Core.Reports;

/// <summary>
/// The statistics behind the recurrence rules.
/// </summary>
public static class RecurrenceRules
{
    #region Constants

    private const double NegligibleTerm = 1e-18;

    #endregion // Constants

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
        ArgumentOutOfRangeException.ThrowIfNegative(trials);
        ArgumentOutOfRangeException.ThrowIfNegative(successes);

        if (double.IsNaN(probability) || probability < 0 || probability > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability), probability, "binomial tail: the probability must be from 0 to 1");
        }

        if (successes == 0 || (probability >= 1 && successes <= trials))
        {
            return 1.0;
        }

        if (successes > trials || probability <= 0)
        {
            return 0.0;
        }

        return Math.Min(1.0, SumTerms(trials, successes, probability));
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
        var reason = UnknownReason.None;

        if (known < ReportLimits.MinKnownIncidents)
        {
            reason = UnknownReason.TooFewIncidents;
        }
        else if (baseKnown <= 0)
        {
            reason = UnknownReason.NoBaseData;
        }

        var outcome = reason == UnknownReason.None ? Decide(matching, known, baseMatching, baseKnown, level) : PatternOutcome.Unknown;

        return new PatternResult
               {
                   Outcome = outcome,
                   Reason = reason,
                   Matching = matching,
                   Known = known,
                   BaseMatching = baseMatching,
                   BaseKnown = baseKnown
               };
    }

    /// <summary>
    /// Decides a rule whose counts are enough: detected with at least two matching outages, a share above the base share and a tail
    /// at most the level.
    /// </summary>
    /// <param name="matching">The outages that match</param>
    /// <param name="known">The outages with a known answer</param>
    /// <param name="baseMatching">The observed hours that match</param>
    /// <param name="baseKnown">The observed hours</param>
    /// <param name="level">The significance level</param>
    /// <returns>Detected or not detected</returns>
    private static PatternOutcome Decide(long matching, long known, long baseMatching, long baseKnown, double level)
    {
        var share = (double)matching / known;
        var baseShare = Math.Clamp((double)baseMatching / baseKnown, 0.0, 1.0);

        if (matching < ReportLimits.MinMatchingIncidents || share <= baseShare)
        {
            return PatternOutcome.NotDetected;
        }

        return BinomialTail(known, matching, baseShare) <= level ? PatternOutcome.Detected : PatternOutcome.NotDetected;
    }

    /// <summary>
    /// Sums the probabilities of the successes from the given count to the number of trials, in logarithms, and stops once the
    /// terms past the mean no longer matter.
    /// </summary>
    /// <param name="trials">The number of trials</param>
    /// <param name="successes">The number of successes, from 1 to the trials</param>
    /// <param name="probability">The probability of one success, above 0 and below 1</param>
    /// <returns>The sum</returns>
    private static double SumTerms(long trials, long successes, double probability)
    {
        var logSuccess = Math.Log(probability);
        var logFailure = Math.Log(1.0 - probability);
        var logOdds = logSuccess - logFailure;
        var smaller = Math.Min(successes, trials - successes);
        var logTerm = (successes * logSuccess) + ((trials - successes) * logFailure);

        for (var step = 1; step <= smaller; step++)
        {
            logTerm += Math.Log((double)(trials - smaller + step) / step);
        }

        var mean = trials * probability;
        var sum = 0.0;

        for (var index = successes; index <= trials; index++)
        {
            var term = Math.Exp(logTerm);

            sum += term;

            if (index > mean && term <= sum * NegligibleTerm)
            {
                break;
            }

            logTerm += Math.Log((double)(trials - index) / (index + 1)) + logOdds;
        }

        return sum;
    }

    #endregion // Methods
}