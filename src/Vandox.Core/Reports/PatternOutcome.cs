namespace Vandox.Core.Reports;

/// <summary>
/// The outcome of a recurrence rule.
/// </summary>
public enum PatternOutcome
{
    /// <summary>
    /// The rule could not be decided.
    /// </summary>
    Unknown,

    /// <summary>
    /// The rule was decided and found no pattern.
    /// </summary>
    NotDetected,

    /// <summary>
    /// The rule was decided and found a pattern.
    /// </summary>
    Detected
}