using System.Globalization;
using System.Runtime.InteropServices;

using Microsoft.Data.Sqlite;

using Vandox.Core.Templates;

namespace Vandox.Storage;

/// <summary>
/// Sums the log lines of one write transaction per template and writes the sums.
/// </summary>
internal sealed class TemplateTally : IDisposable
{
    #region Constants

    private const string UpsertTemplate = "INSERT INTO log_templates(source, program, template, first_seen, last_seen, count) VALUES ($source, $program, $template, $first, $last, $count) "
                                          + "ON CONFLICT(source, program, template) DO UPDATE SET first_seen = min(first_seen, excluded.first_seen), last_seen = max(last_seen, excluded.last_seen), count = count + excluded.count RETURNING id";

    private const string UpsertTemplateHour = "INSERT INTO log_template_hours(template_id, hour, count) VALUES ($id, $hour, $count) ON CONFLICT(template_id, hour) DO UPDATE SET count = count + excluded.count";
    private const string UpsertSourceHour = "INSERT INTO log_source_hours(source, hour, count) VALUES ($source, $hour, $count) ON CONFLICT(source, hour) DO UPDATE SET count = count + excluded.count";

    #endregion // Constants

    #region Fields

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly Dictionary<(string Source, string Program, string Template), (int Slot, long Count, long FirstSeen, long LastSeen)> _templates = [];
    private readonly Dictionary<(int Template, long Hour), long> _templateHours = [];
    private readonly Dictionary<(string Source, long Hour), long> _sourceHours = [];
    private SqliteCommand? _templateCommand;
    private SqliteCommand? _templateHourCommand;
    private SqliteCommand? _sourceHourCommand;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateTally"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    internal TemplateTally(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Counts a log line under its template.
    /// </summary>
    /// <param name="source">The source of the record</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="capturedAt">The capture time in Unix nanoseconds</param>
    internal void Add(string source, string program, string message, long capturedAt)
    {
        var template = LogTemplateNormalizer.Normalize(message);
        ref var totals = ref CollectionsMarshal.GetValueRefOrAddDefault(_templates, (source, program, template), out var exists);

        if (exists)
        {
            totals = (totals.Slot, totals.Count + 1, Math.Min(totals.FirstSeen, capturedAt), Math.Max(totals.LastSeen, capturedAt));
        }
        else
        {
            totals = (_templates.Count - 1, 1, capturedAt, capturedAt);
        }

        var hour = StorageTime.HourStart(capturedAt);

        Increment(_templateHours, (totals.Slot, hour));
        Increment(_sourceHours, (source, hour));
    }

    /// <summary>
    /// Writes the sums; nothing when no line was counted.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write</param>
    internal void Write(CancellationToken cancellationToken)
    {
        if (_templates.Count == 0)
        {
            return;
        }

        var templateCommand = _templateCommand ??= Create(UpsertTemplate, "$source", "$program", "$template", "$first", "$last", "$count");
        var templateHourCommand = _templateHourCommand ??= Create(UpsertTemplateHour, "$id", "$hour", "$count");
        var sourceHourCommand = _sourceHourCommand ??= Create(UpsertSourceHour, "$source", "$hour", "$count");
        var templateValues = Values(templateCommand);
        var templateHourValues = Values(templateHourCommand);
        var sourceHourValues = Values(sourceHourCommand);
        var ids = new long[_templates.Count];

        foreach (var (key, totals) in _templates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            templateValues[0].Value = key.Source;
            templateValues[1].Value = key.Program;
            templateValues[2].Value = key.Template;
            templateValues[3].Value = totals.FirstSeen;
            templateValues[4].Value = totals.LastSeen;
            templateValues[5].Value = totals.Count;
            ids[totals.Slot] = Convert.ToInt64(templateCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        foreach (var (key, count) in _templateHours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            templateHourValues[0].Value = ids[key.Template];
            templateHourValues[1].Value = key.Hour;
            templateHourValues[2].Value = count;
            templateHourCommand.ExecuteNonQuery();
        }

        foreach (var (key, count) in _sourceHours)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceHourValues[0].Value = key.Source;
            sourceHourValues[1].Value = key.Hour;
            sourceHourValues[2].Value = count;
            sourceHourCommand.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds one to the count of a key.
    /// </summary>
    /// <typeparam name="TKey">The type of the key</typeparam>
    /// <param name="counts">The counts</param>
    /// <param name="key">The key</param>
    private static void Increment<TKey>(Dictionary<TKey, long> counts, TKey key)
        where TKey : notnull
    {
        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, key, out _);

        count++;
    }

    /// <summary>
    /// Returns the parameters of a command.
    /// </summary>
    /// <param name="command">The command</param>
    /// <returns>The parameters in the order they were created</returns>
    private static SqliteParameter[] Values(SqliteCommand command)
    {
        return command.Parameters.Cast<SqliteParameter>().ToArray();
    }

    /// <summary>
    /// Creates a command of the transaction with its parameters.
    /// </summary>
    /// <param name="text">The SQL text</param>
    /// <param name="names">The names of the parameters</param>
    /// <returns>The command</returns>
    private SqliteCommand Create(string text, params string[] names)
    {
        var command = _connection.CreateCommand();

        command.Transaction = _transaction;
        command.CommandText = text;

        foreach (var name in names)
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }

        return command;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _templateCommand?.Dispose();
        _templateHourCommand?.Dispose();
        _sourceHourCommand?.Dispose();
    }

    #endregion // IDisposable
}