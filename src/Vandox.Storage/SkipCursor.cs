using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// A cursor over the distinct (source, name) pairs of one table, moved by index seeks.
/// </summary>
internal sealed class SkipCursor
{
    #region Constants

    private const string SourceParameter = "$source";

    private const string NameParameter = "$name";

    #endregion // Constants

    #region Fields

    private readonly string _first;

    private readonly string _firstName;

    private readonly string _nextName;

    private readonly string _nextSource;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipCursor"/> class.
    /// </summary>
    /// <param name="first">The statement of the first source with its first name</param>
    /// <param name="firstName">The statement of the first name of a source</param>
    /// <param name="nextName">The statement of the next name of a source</param>
    /// <param name="nextSource">The statement of the next source</param>
    internal SkipCursor(string first, string firstName, string nextName, string nextSource)
    {
        _first = first;
        _firstName = firstName;
        _nextName = nextName;
        _nextSource = nextSource;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the current metric, or <see langword="null"/> at the end.
    /// </summary>
    internal SeriesKey? Current { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Moves to the first metric.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the cursor stands on the first metric</returns>
    internal async Task StartAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var (source, name) = await ReadPairAsync(connection, _first, [], cancellationToken).ConfigureAwait(false);

        Current = source is null || name is null ? null : new SeriesKey(source, name);
    }

    /// <summary>
    /// Moves to the metric after the current one.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the cursor stands on the next metric</returns>
    internal async Task AdvanceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var current = Current!;
        var sameSource = new Dictionary<string, object>
                         {
                             [SourceParameter] = current.Source,
                             [NameParameter] = current.Name
                         };
        var (name, _) = await ReadPairAsync(connection, _nextName, sameSource, cancellationToken).ConfigureAwait(false);

        if (name is not null)
        {
            Current = new SeriesKey(current.Source, name);

            return;
        }

        var (source, _) = await ReadPairAsync(connection,
                                              _nextSource,
                                              new Dictionary<string, object>
                                              {
                                                  [SourceParameter] = current.Source
                                              },
                                              cancellationToken).ConfigureAwait(false);

        if (source is null)
        {
            Current = null;

            return;
        }

        var (first, _) = await ReadPairAsync(connection,
                                             _firstName,
                                             new Dictionary<string, object>
                                             {
                                                 [SourceParameter] = source
                                             },
                                             cancellationToken).ConfigureAwait(false);

        Current = first is null ? null : new SeriesKey(source, first);
    }

    /// <summary>
    /// Reads the one row of a skip scan statement.
    /// </summary>
    /// <param name="connection">The open reader connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the first and the second column of the row, <see langword="null"/> for a missing column or value</returns>
    private static async Task<(string? First, string? Second)> ReadPairAsync(SqliteConnection connection, string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection,
                                               sql,
                                               parameters,
                                               reader => (reader.IsDBNull(0) ? null : reader.GetString(0), reader.FieldCount > 1 && reader.IsDBNull(1) == false ? reader.GetString(1) : null),
                                               HistoryQueries.Context,
                                               cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : (null, null);
    }

    #endregion // Methods
}