namespace Vandox.Storage;

/// <summary>
/// Folds the process snapshots added since the last step into the per-program 5-minute and hourly buckets and the totals of
/// their source.
/// </summary>
internal static class ProgramRollupFolder
{
    #region Methods

#pragma warning disable RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Runs one fold step in the open transaction of the session.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="policy">The retention policy: buckets past the period of their tier are not created</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the step between rows</param>
    /// <returns>The result of the step</returns>
    internal static RollupStepResult Fold(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003

    #endregion // Methods
}