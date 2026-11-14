using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The statements of one retention step on the writer connection and in its transaction, each prepared once and reused.
/// </summary>
internal sealed class RetentionSession : IDisposable
{
    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly Dictionary<string, TimelineStatement> _statements = [with(StringComparer.Ordinal)];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionSession"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction of the step</param>
    internal RetentionSession(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Tells whether a statement returns a row.
    /// </summary>
    /// <param name="statement">The statement with its parameters set</param>
    /// <returns><c>true</c> when the statement returns a row</returns>
    internal static bool Exists(TimelineStatement statement)
    {
        using var reader = statement.Query();

        return reader.Read();
    }

    /// <summary>
    /// Gets a statement of this step, creating it on first use.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <returns>The statement</returns>
    internal TimelineStatement Statement(string sql)
    {
        if (_statements.TryGetValue(sql, out var statement))
        {
            return statement;
        }

        statement = new TimelineStatement(_connection, _transaction, sql);
        _statements[sql] = statement;

        return statement;
    }

    /// <summary>
    /// Reads a number stored in <c>meta</c>.
    /// </summary>
    /// <param name="key">The key</param>
    /// <returns>The number, or 0 when the key is not set</returns>
    internal long ReadNumber(string key)
    {
        using var reader = Statement(RetentionQueries.ReadMeta).Set("$key", key).Query();

        return reader.Read() && long.TryParse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>
    /// Stores a number in <c>meta</c>.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The number</param>
    internal void WriteNumber(string key, long value)
    {
        Statement(RetentionQueries.WriteMeta).Set("$key", key).Set("$value", value.ToString(CultureInfo.InvariantCulture)).Execute();
    }

    /// <summary>
    /// Reads the highest record ID.
    /// </summary>
    /// <returns>The ID, or 0 when no record is stored</returns>
    internal long ReadHighestId()
    {
        using var reader = Statement(RetentionQueries.HighestId).Query();

        return reader.Read() ? reader.GetInt64(0) : 0;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var statement in _statements.Values)
        {
            statement.Dispose();
        }

        _statements.Clear();
    }

    #endregion // IDisposable
}