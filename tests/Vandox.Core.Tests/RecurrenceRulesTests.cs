using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="RecurrenceRules"/>
/// </summary>
[TestClass]
public class RecurrenceRulesTests
{
    #region Constants

    private const double OneInTwentyFour = 1.0 / 24;

    private const double Level = 0.01 / 24;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The binomial tail matches reference values to a relative 1e-9.
    /// </summary>
    /// <param name="trials">The number of trials</param>
    /// <param name="successes">The number of successes</param>
    /// <param name="probability">The probability of a success</param>
    /// <param name="expected">The reference value</param>
    [TestMethod]
    [DataRow(3L, 3L, 1.0 / 24, 7.233796296296295e-05)]
    [DataRow(3L, 2L, 1.0 / 24, 0.005063657407407407)]
    [DataRow(5L, 3L, 0.06, 0.0019702655999999995)]
    [DataRow(10L, 3L, 0.1, 0.07019082640000002)]
    [DataRow(20L, 5L, 0.1, 0.04317449528446341)]
    [DataRow(1000L, 30L, 0.01, 2.0599888509719412e-07)]
    [DataRow(10L, 4L, 1.0 / 24, 0.0005168454002460099)]
    public void BinomialTailMatchesReferenceValues(long trials, long successes, double probability, double expected)
    {
        // Act
        var actual = RecurrenceRules.BinomialTail(trials, successes, probability);

        // Assert
        Assert.AreEqual(expected, actual, Math.Abs(expected) * 1e-9, $"tail({trials}, {successes}, {probability})");
    }

    /// <summary>
    /// Zero successes are certain, more successes than trials impossible, and the probabilities 0 and 1 are exact.
    /// </summary>
    /// <param name="trials">The number of trials</param>
    /// <param name="successes">The number of successes</param>
    /// <param name="probability">The probability of a success</param>
    /// <param name="expected">The expected tail</param>
    [TestMethod]
    [DataRow(10L, 0L, 0.3, 1.0)]
    [DataRow(0L, 0L, 0.3, 1.0)]
    [DataRow(7L, 8L, 0.3, 0.0)]
    [DataRow(10L, 2L, 0.0, 0.0)]
    [DataRow(4L, 4L, 1.0, 1.0)]
    public void BinomialTailHandlesEdges(long trials, long successes, double probability, double expected)
    {
        // Act
        var actual = RecurrenceRules.BinomialTail(trials, successes, probability);

        // Assert
        Assert.AreEqual(expected, actual, 1e-12, $"tail({trials}, {successes}, {probability})");
    }

    /// <summary>
    /// Negative counts and a probability outside zero to one are refused.
    /// </summary>
    /// <param name="trials">The number of trials</param>
    /// <param name="successes">The number of successes</param>
    /// <param name="probability">The probability of a success</param>
    [TestMethod]
    [DataRow(-1L, 0L, 0.5)]
    [DataRow(3L, -1L, 0.5)]
    [DataRow(3L, 1L, -0.1)]
    [DataRow(3L, 1L, 1.1)]
    [DataRow(3L, 1L, double.NaN)]
    public void BinomialTailRefusesInvalidArguments(long trials, long successes, double probability)
    {
        // Act and Assert
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RecurrenceRules.BinomialTail(trials, successes, probability), $"tail({trials}, {successes}, {probability})");
    }

    /// <summary>
    /// Fewer than three known outages make the result unknown, whatever the other numbers are.
    /// </summary>
    [TestMethod]
    public void EvaluateIsUnknownWithFewerThanThreeKnown()
    {
        // Act
        var result = RecurrenceRules.Evaluate(2, 2, 3, 72, Level);
        var none = RecurrenceRules.Evaluate(0, 0, 0, 0, Level);

        // Assert
        Assert.AreEqual(PatternOutcome.Unknown, result.Outcome, "two known outages");
        Assert.AreEqual(UnknownReason.TooFewIncidents, result.Reason, "reason");
        Assert.AreEqual(2, result.Matching, "matching is kept");
        Assert.AreEqual(2, result.Known, "known is kept");
        Assert.AreEqual(3, result.BaseMatching, "base matching is kept");
        Assert.AreEqual(72, result.BaseKnown, "base known is kept");
        Assert.AreEqual(PatternOutcome.Unknown, none.Outcome, "nothing known at all");
        Assert.AreEqual(UnknownReason.TooFewIncidents, none.Reason, "too few incidents comes before no base data");
    }

    /// <summary>
    /// Without a known base hour the result is unknown with the reason no base data.
    /// </summary>
    [TestMethod]
    public void EvaluateIsUnknownWithoutBaseData()
    {
        // Act
        var result = RecurrenceRules.Evaluate(3, 3, 0, 0, Level);

        // Assert
        Assert.AreEqual(PatternOutcome.Unknown, result.Outcome, "outcome");
        Assert.AreEqual(UnknownReason.NoBaseData, result.Reason, "reason");
        Assert.IsNull(result.BaseShare, "no base share");
        Assert.AreEqual(1.0, result.Share!.Value, 1e-12, "share of the outages");
    }

    /// <summary>
    /// A share that is far above the base share and has a tail at most the level is detected.
    /// </summary>
    /// <param name="matching">The outages with the property</param>
    /// <param name="known">The known outages</param>
    [TestMethod]
    [DataRow(3L, 3L)]
    [DataRow(5L, 10L)]
    public void EvaluateDetectsUnlikelyShare(long matching, long known)
    {
        // Act
        var result = RecurrenceRules.Evaluate(matching, known, 3, 72, Level);

        // Assert
        Assert.AreEqual(PatternOutcome.Detected, result.Outcome, $"{matching} of {known}");
        Assert.AreEqual(UnknownReason.None, result.Reason, "no reason for a decided result");
        Assert.AreEqual(matching, result.Matching, "matching");
        Assert.AreEqual(known, result.Known, "known");
        Assert.AreEqual(OneInTwentyFour, result.BaseShare!.Value, 1e-12, "base share");
    }

    /// <summary>
    /// A share whose tail is above the level is not detected.
    /// </summary>
    /// <param name="matching">The outages with the property</param>
    /// <param name="known">The known outages</param>
    [TestMethod]
    [DataRow(2L, 3L)]
    [DataRow(4L, 10L)]
    public void EvaluateDoesNotDetectLikelyShare(long matching, long known)
    {
        // Act
        var result = RecurrenceRules.Evaluate(matching, known, 3, 72, Level);

        // Assert
        Assert.AreEqual(PatternOutcome.NotDetected, result.Outcome, $"{matching} of {known}");
        Assert.AreEqual(UnknownReason.None, result.Reason, "no reason for a decided result");
    }

    /// <summary>
    /// At least two outages must have the property and their share must be above the base share, even when the tail is small.
    /// </summary>
    [TestMethod]
    public void EvaluateNeedsTwoMatchingAndShareAboveBase()
    {
        // Act
        var single = RecurrenceRules.Evaluate(1, 3, 0, 72, 1.0);
        var equal = RecurrenceRules.Evaluate(3, 6, 5, 10, 1.0);
        var below = RecurrenceRules.Evaluate(2, 6, 5, 10, 1.0);

        // Assert
        Assert.AreEqual(PatternOutcome.NotDetected, single.Outcome, "one matching outage is never enough");
        Assert.AreEqual(PatternOutcome.NotDetected, equal.Outcome, "a share equal to the base share is not above it");
        Assert.AreEqual(PatternOutcome.NotDetected, below.Outcome, "a share below the base share");
    }

    #endregion // Methods
}