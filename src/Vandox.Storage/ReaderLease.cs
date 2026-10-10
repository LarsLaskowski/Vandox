using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// A reader connection that gives its slot back when it is disposed.
/// </summary>
internal sealed class ReaderLease : IAsyncDisposable
{
    #region Fields

    private readonly SemaphoreSlim _slots;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ReaderLease"/> class.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="slots">The slots to give back</param>
    internal ReaderLease(SqliteConnection connection, SemaphoreSlim slots)
    {
        Connection = connection;
        _slots = slots;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the reader connection.
    /// </summary>
    internal SqliteConnection Connection { get; }

    #endregion // Properties

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        _slots.Release();
    }

    #endregion // IAsyncDisposable
}