namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Lets one log line of a kind through per minute and counts the refusals suppressed in between.
/// </summary>
internal sealed class IngestRefusalLog
{
    #region Fields

    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestRefusalLog"/> class.
    /// </summary>
    /// <param name="clock">The clock</param>
    internal IngestRefusalLog(TimeProvider clock)
    {
        _clock = clock;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Asks whether a refusal may be logged now.
    /// </summary>
    /// <param name="suppressed">The number of refusals suppressed since the last line that was let through</param>
    /// <returns><c>true</c> when the line may be written</returns>
    internal bool TryTake(out int suppressed)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}