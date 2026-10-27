using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// A statement of a write transaction whose command and parameters are created once and set by name before each run.
/// </summary>
internal sealed partial class TimelineStatement : IDisposable
{
    #region Fields

    private readonly SqliteCommand _command;
    private readonly Dictionary<string, SqliteParameter> _parameters = new(StringComparer.Ordinal);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TimelineStatement"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="sql">The statement; its parameters start with <c>$</c></param>
    internal TimelineStatement(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        _command = connection.CreateCommand();
        _command.Transaction = transaction;
        _command.CommandText = sql;

        foreach (var name in ParameterPattern().Matches(sql).Select(match => match.Value).Distinct())
        {
            var parameter = new SqliteParameter(name, DBNull.Value);

            _parameters[name] = parameter;
            _command.Parameters.Add(parameter);
        }
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Sets a parameter.
    /// </summary>
    /// <param name="name">The name with its <c>$</c></param>
    /// <param name="value">The value; <see langword="null"/> is stored as NULL</param>
    /// <returns>The statement</returns>
    internal TimelineStatement Set(string name, object? value)
    {
        _parameters[name].Value = value ?? DBNull.Value;

        return this;
    }

    /// <summary>
    /// Runs the statement and returns the number of rows it changed.
    /// </summary>
    /// <returns>The number of rows</returns>
    internal int Execute()
    {
        return _command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs the statement and returns its rows.
    /// </summary>
    /// <returns>The reader, which the caller disposes</returns>
    internal SqliteDataReader Query()
    {
        return _command.ExecuteReader();
    }

    [GeneratedRegex(@"\$[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterPattern();

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _command.Dispose();
    }

    #endregion // IDisposable
}