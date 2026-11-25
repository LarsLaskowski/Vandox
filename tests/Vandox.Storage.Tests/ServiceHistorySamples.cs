using Microsoft.Data.Sqlite;

using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Builds records and rows and runs bulk statements for the tests of the services and MariaDB reads.
/// </summary>
internal static class ServiceHistorySamples
{
    #region Constants

    /// <summary>
    /// The source of the MariaDB status records.
    /// </summary>
    internal const string MariaDbSource = "mariadb";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Gets an instant a number of minutes after <see cref="Samples.Base"/>.
    /// </summary>
    /// <param name="minutes">The minutes</param>
    /// <returns>The instant</returns>
    internal static DateTimeOffset At(double minutes)
    {
        return Samples.Base.AddMinutes(minutes);
    }

    /// <summary>
    /// Creates a MariaDB status record of the source <c>mariadb</c>.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="availability">The availability</param>
    /// <param name="memoryUsed">The status value <c>Memory_used</c>, or <c>null</c> for none</param>
    /// <param name="threadsConnected">The status value <c>Threads_connected</c>, or <c>null</c> for none</param>
    /// <param name="threads">The recorded threads, none for a record without a snapshot</param>
    /// <returns>The record</returns>
    internal static DataRecord Status(DateTimeOffset capturedAt, string availability, ulong? memoryUsed, ulong? threadsConnected, params MariaDbThread[] threads)
    {
        var values = new Dictionary<string, ulong>();

        if (memoryUsed is not null)
        {
            values["Memory_used"] = memoryUsed.Value;
        }

        if (threadsConnected is not null)
        {
            values["Threads_connected"] = threadsConnected.Value;
        }

        return new DataRecord
               {
                   Origin = RecordOrigin.Backend,
                   Source = MariaDbSource,
                   CapturedAt = capturedAt,
                   Data = new MariaDbStatus
                          {
                              Availability = availability,
                              Status = values.Count > 0 ? values : null,
                              Threads = threads.Length > 0 ? [.. threads] : null,
                              Complete = true
                          }
               };
    }

    /// <summary>
    /// Creates a recorded thread.
    /// </summary>
    /// <param name="id">The connection ID</param>
    /// <param name="command">The command, empty for none</param>
    /// <param name="timeSeconds">The seconds the thread has been in its state</param>
    /// <returns>The thread</returns>
    internal static MariaDbThread Thread(ulong id, string command, ulong timeSeconds)
    {
        return new MariaDbThread
               {
                   Id = id,
                   Command = command,
                   TimeSeconds = timeSeconds
               };
    }

    /// <summary>
    /// Inserts a <c>service_state</c> record directly.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="unit">The unit</param>
    /// <param name="activeState">The active state</param>
    /// <param name="subState">The sub state</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the record is stored</returns>
    internal static async Task InsertServiceAsync(string directory, string unit, string activeState, string subState, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO records(kind, origin, source, captured_at, received_at, data) "
                              + "VALUES ('service_state', 'backend', 'services', $at, $at, json_object('unit', $unit, 'load_state', 'loaded', 'active_state', $active, 'sub_state', $sub, 'restarts', 0))";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$at", StorageTime.ToNanoseconds(capturedAt));
        command.Parameters.AddWithValue("$unit", unit);
        command.Parameters.AddWithValue("$active", activeState);
        command.Parameters.AddWithValue("$sub", subState);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts <c>service_state</c> records of units named <c>unit000</c>, <c>unit001</c> and so on, one record per unit, a second apart.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="count">The number of units</param>
    /// <param name="first">The capture time of the first record</param>
    /// <param name="descending">Whether the record of the unit with the highest name comes first</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task InsertUnitsAsync(string directory, int count, DateTimeOffset first, bool descending, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) "
                              + "INSERT INTO records(kind, origin, source, captured_at, received_at, data) "
                              + "SELECT 'service_state', 'backend', 'services', $first + i * 1000000000, $first + i * 1000000000, "
                              + "json_object('unit', printf('unit%03d', CASE WHEN $descending THEN $count - 1 - i ELSE i END), 'load_state', 'loaded', 'active_state', 'active', 'sub_state', 'running', 'restarts', 0) FROM n";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$first", StorageTime.ToNanoseconds(first));
        command.Parameters.AddWithValue("$descending", descending ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts <c>gap</c> records of a collector directly, each captured at the end of its span.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="collector">The collector, empty for none</param>
    /// <param name="count">The number of records</param>
    /// <param name="firstFrom">The start of the span of the first record</param>
    /// <param name="spacing">The distance of the starts of the spans</param>
    /// <param name="length">The length of each span</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task InsertCollectorGapsAsync(string directory, string collector, int count, DateTimeOffset firstFrom, TimeSpan spacing, TimeSpan length, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) "
                              + "INSERT INTO records(kind, origin, source, agent_id, captured_at, received_at, data) "
                              + "SELECT 'gap', 'backend', 'agent', 'agent-1', $first + i * $spacing + $length, $first + i * $spacing + $length, "
                              + "json_object('from', strftime('%Y-%m-%dT%H:%M:%SZ', ($first + i * $spacing) / 1000000000, 'unixepoch'), "
                              + "'to', strftime('%Y-%m-%dT%H:%M:%SZ', ($first + i * $spacing + $length) / 1000000000, 'unixepoch'), 'cause', 'no_data', 'collector', $collector) FROM n";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$first", StorageTime.ToNanoseconds(firstFrom));
        command.Parameters.AddWithValue("$spacing", spacing.Ticks * 100);
        command.Parameters.AddWithValue("$length", length.Ticks * 100);
        command.Parameters.AddWithValue("$collector", collector);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts <c>mariadb_status</c> records of one availability directly, spaced evenly, each with the status values <c>Memory_used</c> and <c>Threads_connected</c> when it is up.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="first">The capture time of the first record</param>
    /// <param name="count">The number of records</param>
    /// <param name="spacing">The distance of the capture times</param>
    /// <param name="availability">The availability</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the records are stored</returns>
    internal static async Task InsertStatusRunAsync(string directory, DateTimeOffset first, int count, TimeSpan spacing, string availability, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < $count) "
                              + "INSERT INTO records(kind, origin, source, captured_at, received_at, data) "
                              + "SELECT 'mariadb_status', 'backend', 'mariadb', $first + i * $spacing, $first + i * $spacing, "
                              + "CASE WHEN $availability = 'up' THEN json_object('availability', 'up', 'status', json_object('Memory_used', 1000 + i, 'Threads_connected', 7), 'complete', json('true')) "
                              + "ELSE json_object('availability', $availability, 'complete', json('true')) END FROM n";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$first", StorageTime.ToNanoseconds(first));
        command.Parameters.AddWithValue("$spacing", spacing.Ticks * 100);
        command.Parameters.AddWithValue("$availability", availability);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts a record of the kind <c>mariadb_status</c> and the source <c>mariadb</c> with a payload text that no validated write would store.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="data">The payload text</param>
    /// <param name="cancellationToken">Cancels the statement</param>
    /// <returns>A task that completes when the record is stored</returns>
    internal static async Task InsertForgedStatusAsync(string directory, DateTimeOffset capturedAt, string data, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal
        command.CommandText = "INSERT INTO records(kind, origin, source, captured_at, received_at, data) VALUES ('mariadb_status', 'backend', 'mariadb', $at, $at, $data)";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$at", StorageTime.ToNanoseconds(capturedAt));
        command.Parameters.AddWithValue("$data", data);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the query plan of a statement.
    /// </summary>
    /// <param name="directory">The store directory</param>
    /// <param name="sql">The statement, a test-owned literal, with the parameters <c>$from</c>, <c>$to</c> and <c>$limit</c></param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The detail lines of the plan, one per line</returns>
    internal static async Task<string> PlanAsync(string directory, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={RetentionSamples.DatabasePath(directory)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

#pragma warning disable CA2100 // the statement is a test-owned literal passed by the calling test
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$from", StorageTime.ToNanoseconds(Samples.Base));
        command.Parameters.AddWithValue("$to", StorageTime.ToNanoseconds(Samples.Base.AddHours(1)));
        command.Parameters.AddWithValue("$limit", 51);

        var lines = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join("\n", lines);
    }

    #endregion // Methods
}