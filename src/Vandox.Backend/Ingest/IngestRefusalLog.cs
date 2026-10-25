namespace Vandox.Backend.Ingest;

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

    /// <summary>
    /// Guards the state below.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The refusals not logged since the last line.
    /// </summary>
    private int _suppressed;

    /// <summary>
    /// The time of the last line, <c>null</c> before the first one.
    /// </summary>
    private DateTimeOffset? _last;

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
        var now = _clock.GetUtcNow();

        lock (_lock)
        {
            if (_last is null || now - _last.Value >= TimeSpan.FromMinutes(1))
            {
                suppressed = _suppressed;
                _suppressed = 0;
                _last = now;

                return true;
            }

            _suppressed++;
            suppressed = 0;

            return false;
        }
    }

    #endregion // Methods
}