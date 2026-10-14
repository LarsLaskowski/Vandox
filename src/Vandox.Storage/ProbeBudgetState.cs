namespace Vandox.Storage;

/// <summary>
/// The state of the progress handler that stops a statement when the token is cancelled or a time budget is used up.
/// </summary>
internal sealed class ProbeBudgetState
{
    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeBudgetState"/> class and takes the start timestamp.
    /// </summary>
    /// <param name="timeProvider">The clock of the budget</param>
    /// <param name="budget">The time the statement may take</param>
    /// <param name="cancellationToken">The token whose cancellation stops the statement</param>
    internal ProbeBudgetState(TimeProvider timeProvider, TimeSpan budget, CancellationToken cancellationToken)
    {
        TimeProvider = timeProvider;
        Budget = budget;
        CancellationToken = cancellationToken;
        Start = timeProvider.GetTimestamp();
    }

    #endregion // Constructor

    #region Properties

    /// <summary>
    /// Gets the clock of the budget.
    /// </summary>
    internal TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the time the statement may take.
    /// </summary>
    internal TimeSpan Budget { get; }

    /// <summary>
    /// Gets the token whose cancellation stops the statement.
    /// </summary>
    internal CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the timestamp of the start.
    /// </summary>
    internal long Start { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the handler stopped the statement because the budget was used up.
    /// </summary>
    internal bool Exhausted { get; set; }

    #endregion // Properties
}