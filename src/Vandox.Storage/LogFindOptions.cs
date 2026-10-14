namespace Vandox.Storage;

/// <summary>
/// The bounds and the clock of one call of the log finder.
/// </summary>
internal sealed class LogFindOptions
{
    #region Properties

    /// <summary>
    /// Gets or sets the index match count below which a text search runs from the index.
    /// </summary>
    internal int SelectiveMatches { get; set; }

    /// <summary>
    /// Gets or sets the time the index check may take.
    /// </summary>
    internal TimeSpan ProbeBudget { get; set; }

    /// <summary>
    /// Gets or sets the time a scan page may take, counted from the start of the call; <see cref="Timeout.InfiniteTimeSpan"/> for no limit.
    /// </summary>
    internal TimeSpan PageBudget { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets or sets the clock of the budgets.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    #endregion // Properties
}