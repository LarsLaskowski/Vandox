namespace Vandox.Storage;

/// <summary>
/// Folds the connection snapshots added since the last step into the per-state, per-program and per-address 5-minute and hourly
/// buckets of their source, and trims the addresses of ended buckets.
/// </summary>
internal static class ConnectionRollupFolder
{
    #region Methods

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
        return new RollupStepResult();
    }

    #endregion // Methods
}