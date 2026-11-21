using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// Runs the store reads of the history view and of the processes view.
/// </summary>
internal static class StoreReads
{
    #region Methods

    /// <summary>
    /// Runs a read; a refusal of the store is a failure of the view, not a text for the user.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="view">The name of the view, for the message of the failure</param>
    /// <param name="read">The read</param>
    /// <returns>A task that returns the result</returns>
    /// <exception cref="InvalidOperationException">The store refused the read</exception>
    internal static async Task<TResult> ReadingAsync<TResult>(string view, Func<Task<TResult>> read)
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
        catch (InvalidQueryException exception)
        {
            throw new InvalidOperationException($"{view}: a read was refused", exception);
        }
    }

    #endregion // Methods
}