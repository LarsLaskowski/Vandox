namespace Vandox.Core.Reports;

/// <summary>
/// A yes or no judgement that can also be unknown.
/// </summary>
public enum Indicator
{
    /// <summary>
    /// The data to decide is missing.
    /// </summary>
    Unknown,

    /// <summary>
    /// The thing did not happen.
    /// </summary>
    No,

    /// <summary>
    /// The thing happened.
    /// </summary>
    Yes
}