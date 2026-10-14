using Microsoft.Data.Sqlite;

using SQLitePCL;

namespace Vandox.Storage;

/// <summary>
/// A private in-memory FTS5 index for the lines of one chunk of a scan; it matches multi-part terms with the tokenizer of the stored index.
/// The connection is disposed on every path.
/// </summary>
internal sealed class LineIndex : IDisposable
{
    #region Constants

    /// <summary>
    /// The largest number of lines inserted in one chunk.
    /// </summary>
    internal const int ChunkLines = 64;

    /// <summary>
    /// The number of characters of messages at which a chunk is matched although it has fewer lines than <see cref="ChunkLines"/>; it bounds the work of one match.
    /// </summary>
    internal const int ChunkCharacters = 262144;

    /// <summary>
    /// The tokenizer of the stored index.
    /// </summary>
    internal const string Tokenizer = "unicode61 remove_diacritics 2";

    #endregion // Constants

    #region Fields

    private readonly SqliteConnection _connection;

    private bool _unusable;

    #endregion // Fields

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the <see cref="LineIndex"/> class.
    /// </summary>
    /// <param name="connection">The open in-memory connection with the table</param>
    private LineIndex(SqliteConnection connection)
    {
        _connection = connection;
    }

    #endregion // Constructor

    #region Methods

    /// <summary>
    /// Opens an empty in-memory index.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls on the index</param>
    /// <returns>The index</returns>
    internal static LineIndex Open(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        LineIndex? index = null;

        try
        {
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"CREATE VIRTUAL TABLE line_fts USING fts5(message, tokenize='{Tokenizer}')";
                command.ExecuteNonQuery();
            }

            ReaderQuery.Attach(connection, cancellationToken);
            index = new LineIndex(connection);

            return index;
        }
        finally
        {
            if (index is null)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns the IDs of the lines whose message matches an expression; every inserted row is deleted before the call returns or throws.
    /// </summary>
    /// <param name="lines">The lines of the chunk</param>
    /// <param name="match">The FTS5 expression</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the IDs of the matching lines</returns>
    internal async Task<HashSet<long>> MatchAsync(IReadOnlyList<(long Id, string Message)> lines, string match, CancellationToken cancellationToken)
    {
        if (_unusable)
        {
            throw new InvalidOperationException("store: the line index could not be emptied after an error");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReaderQuery.Attach(_connection, cancellationToken);

        var matched = new HashSet<long>();
        var completed = false;

        try
        {
            await InsertAsync(lines, cancellationToken).ConfigureAwait(false);

            using var command = _connection.CreateCommand();

            command.CommandText = "SELECT rowid FROM line_fts WHERE line_fts MATCH $match";
            command.Parameters.AddWithValue("$match", match);

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                matched.Add(reader.GetInt64(0));
            }

            completed = true;
        }
        finally
        {
            Empty(completed);
        }

        return matched;
    }

    /// <summary>
    /// Inserts the lines of a chunk; the messages are bound parameters only.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the lines are inserted</returns>
    private async Task InsertAsync(IReadOnlyList<(long Id, string Message)> lines, CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();

        command.CommandText = "INSERT INTO line_fts(rowid, message) VALUES ($id, $message)";

        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var message = command.Parameters.Add("$message", SqliteType.Text);

        foreach (var (lineId, text) in lines)
        {
            id.Value = lineId;
            message.Value = text;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes every row. The handler of the call is removed first, because a cancelled token would stop the delete. A delete that fails after an
    /// earlier error does not replace it: the index is then marked unusable; a delete that fails after a successful match is thrown.
    /// </summary>
    /// <param name="completed"><c>true</c> when the match ended without an error</param>
    private void Empty(bool completed)
    {
        raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);

        if (completed)
        {
            Delete();

            return;
        }

        try
        {
            Delete();
        }
        catch (SqliteException)
        {
            _unusable = true;
        }
    }

    /// <summary>
    /// Deletes every row of the table.
    /// </summary>
    private void Delete()
    {
        using var command = _connection.CreateCommand();

        command.CommandText = "DELETE FROM line_fts";
        command.ExecuteNonQuery();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
    }

    #endregion // IDisposable
}