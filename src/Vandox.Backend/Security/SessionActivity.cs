namespace Vandox.Backend.Security;

/// <summary>
/// Tells whether the activity of a circuit may extend its session; one instance per circuit.
/// </summary>
public sealed class SessionActivity
{
    #region Fields

    /// <summary>
    /// The number of open pauses.
    /// </summary>
    private int _pauses;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets a value indicating whether at least one pause is open.
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _pauses) > 0;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Opens a pause: while one is open, the activity of the circuit does not extend its session.
    /// </summary>
    /// <returns>A scope that ends the pause once when it is disposed</returns>
    public IDisposable Pause()
    {
        Interlocked.Increment(ref _pauses);

        // Skeleton: removed with the implementation
#pragma warning disable RH2003
        throw new NotImplementedException();
#pragma warning restore RH2003
    }

    #endregion // Methods
}