#pragma warning disable RH2003, S2325

namespace Vandox.Backend.Logs;

/// <summary>
/// Limits the reads of the web views in flight across all callers.
/// </summary>
public sealed class WebReaderGate : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WebReaderGate"/> class.
    /// </summary>
    /// <param name="maxConcurrent">The number of slots, 1 to 3</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot count is not 1 to 3</exception>
    public WebReaderGate(int maxConcurrent)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Waits for a slot with the token of the call, runs the call and gives the slot back.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="call">The call</param>
    /// <param name="cancellationToken">Ends the wait for a slot</param>
    /// <returns>A task that returns the result of the call</returns>
    public Task<TResult> RunAsync<TResult>(Func<Task<TResult>> call, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    #endregion // IDisposable
}