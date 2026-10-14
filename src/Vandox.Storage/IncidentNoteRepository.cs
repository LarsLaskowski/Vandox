using System.Buffers;
using System.Text;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The SQL of the incident notes.
/// </summary>
internal static class IncidentNoteRepository
{
    #region Constants

    private const string Prefix = "store: invalid note: ";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Validates an edit.
    /// </summary>
    /// <param name="edit">The edit</param>
    /// <exception cref="InvalidNoteException">The edit breaks a rule</exception>
    internal static void Validate(IncidentNoteEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.Text);

        Require(edit.IncidentId > 0, "incident ID must be positive");
        Require(edit.WrittenFor > 0, "written for must be positive");
        Require(edit.Text.Length <= StorageLimits.MaxNoteChars, $"text is longer than {StorageLimits.MaxNoteChars} characters");
        Require(edit.Text.IndexOf('\0') < 0, "text must not contain U+0000");
        Require(IsWellFormed(edit.Text), "text is not valid UTF-16");
        Require(StorageTime.InStorableRange(edit.SavedAt), "saved at must be within the storable range");
    }

    /// <summary>
    /// Saves a validated edit within a transaction.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="edit">The edit</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The outcome</returns>
    internal static async Task<IncidentNoteOutcome> SaveAsync(SqliteConnection connection, SqliteTransaction transaction, IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        using var exists = Command(connection, transaction, "SELECT 1 FROM incidents WHERE id = $incident");

        exists.Parameters.AddWithValue("$incident", edit.IncidentId);

        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return IncidentNoteOutcome.NotFound;
        }

        var own = edit.WrittenFor == edit.IncidentId;

        if (edit.Text.Length == 0)
        {
            var deleted = await ExecuteAsync(connection, transaction, "DELETE FROM incident_notes WHERE written_for = $written AND incident_id = $incident", edit, cancellationToken).ConfigureAwait(false);

            return deleted > 0 || own ? IncidentNoteOutcome.Deleted : IncidentNoteOutcome.NotFound;
        }

        var sql = own
                      ? "INSERT INTO incident_notes (written_for, incident_id, text, updated_at) VALUES ($written, $incident, $text, $saved) ON CONFLICT(written_for) DO UPDATE SET text = excluded.text, updated_at = excluded.updated_at WHERE incident_notes.incident_id = excluded.incident_id"
                      : "UPDATE incident_notes SET text = $text, updated_at = $saved WHERE written_for = $written AND incident_id = $incident";
        var changed = await ExecuteAsync(connection, transaction, sql, edit, cancellationToken).ConfigureAwait(false);

        return changed > 0 ? IncidentNoteOutcome.Saved : IncidentNoteOutcome.NotFound;
    }

    /// <summary>
    /// Moves the notes of an incident to another one.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="fromIncident">The incident that goes away</param>
    /// <param name="toIncident">The incident that keeps the notes</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>A task that completes when the notes are moved</returns>
    internal static async Task MoveAsync(SqliteConnection connection, SqliteTransaction transaction, long fromIncident, long toIncident, CancellationToken cancellationToken)
    {
        using var move = Command(connection, transaction, "UPDATE incident_notes SET incident_id = $kept WHERE incident_id = $other");

        move.Parameters.AddWithValue("$kept", toIncident);
        move.Parameters.AddWithValue("$other", fromIncident);
        await move.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the statement that reads the notes of an incident, one more than the page holds.
    /// </summary>
    /// <param name="incidentId">The number of the incident</param>
    /// <returns>The SQL and its parameters</returns>
    internal static (string Sql, Dictionary<string, object> Parameters) BuildRead(long incidentId)
    {
        var parameters = new Dictionary<string, object>
                         {
                             ["$id"] = incidentId,
                             ["$limit"] = StorageLimits.MaxNotesRead + 1L
                         };

        return ("SELECT written_for, incident_id, text, updated_at FROM incident_notes WHERE incident_id = $id ORDER BY written_for LIMIT $limit", parameters);
    }

    /// <summary>
    /// Reads a note row.
    /// </summary>
    /// <param name="reader">The reader, positioned on a row of <see cref="BuildRead"/></param>
    /// <returns>The note</returns>
    internal static IncidentNote Read(SqliteDataReader reader)
    {
        return new IncidentNote
               {
                   WrittenFor = reader.GetInt64(0),
                   IncidentId = reader.GetInt64(1),
                   Text = reader.GetString(2),
                   UpdatedAt = StorageTime.FromNanoseconds(reader.GetInt64(3))
               };
    }

    /// <summary>
    /// Tells whether a text holds no lone surrogate.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when every surrogate is part of a pair</returns>
    private static bool IsWellFormed(string text)
    {
        var rest = text.AsSpan();

        while (rest.Length > 0)
        {
            if (Rune.DecodeFromUtf16(rest, out _, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            rest = rest[consumed..];
        }

        return true;
    }

    /// <summary>
    /// Runs a statement that takes the fields of an edit.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="sql">The statement</param>
    /// <param name="edit">The edit</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>The number of changed rows</returns>
    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, IncidentNoteEdit edit, CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, sql);

        command.Parameters.AddWithValue("$written", edit.WrittenFor);
        command.Parameters.AddWithValue("$incident", edit.IncidentId);

        if (edit.Text.Length > 0)
        {
            command.Parameters.AddWithValue("$text", edit.Text);
            command.Parameters.AddWithValue("$saved", StorageTime.ToNanoseconds(edit.SavedAt));
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a command within the transaction.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="transaction">The transaction</param>
    /// <param name="sql">The statement</param>
    /// <returns>The command</returns>
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();

        command.Transaction = transaction;
        command.CommandText = sql;

        return command;
    }

    /// <summary>
    /// Throws an error unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="rule">What must hold</param>
    private static void Require(bool condition, string rule)
    {
        if (condition)
        {
            return;
        }

        throw new InvalidNoteException(Prefix + rule);
    }

    #endregion // Methods
}