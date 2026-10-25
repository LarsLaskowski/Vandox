using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Ingest;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An ingest endpoint with a fake clock, a recording writer (in front of a real store when asked), a scripted free space and a log capture,
/// and the means to read back what the store holds.
/// </summary>
internal sealed class IngestRig : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The token the rig's endpoint accepts.
    /// </summary>
    internal const string Token = "ingest-token-0123456789-abcdefghijklmnop";

    #endregion // Constants

    #region Fields

    private readonly TempDirectory? _directory;

    private readonly SqliteStore? _store;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestRig"/> class.
    /// </summary>
    /// <param name="directory">The directory of the real store, <c>null</c> for none</param>
    /// <param name="store">The real store, <c>null</c> for none</param>
    /// <param name="enabled">Whether the token and the agent ID are configured</param>
    /// <param name="minFree">The value of <c>ingest.min_free</c></param>
    private IngestRig(TempDirectory? directory, SqliteStore? store, bool enabled, string minFree)
    {
        _directory = directory;
        _store = store;
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
        Writer = new RecordingRecordWriter(store);
        Log = new IngestLogCapture();
        Lifetime = new ControlledApplicationLifetime();

        var config = new BackendConfig();

        config.Ingest.MinFree = minFree;

        if (enabled)
        {
            config.Ingest.AgentId = IngestSamples.AgentId;
            config.Secrets.AgentToken = new Secret(Token);
        }

        Settings = IngestSettings.Create(config, Log);
        Endpoint = new IngestEndpoint(Settings, Writer, new FreeSpace(directory?.Path ?? "unused", _ => OnProbe is null ? FreeBytes : OnProbe()), Lifetime, Clock, Log.CreateFactory());
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the fake clock of the endpoint, at 2026-10-25 12:00:00 UTC to start with.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the writer the endpoint writes to.
    /// </summary>
    internal RecordingRecordWriter Writer { get; }

    /// <summary>
    /// Gets the log lines of the endpoint and of the settings.
    /// </summary>
    internal IngestLogCapture Log { get; }

    /// <summary>
    /// Gets the application lifetime of the endpoint.
    /// </summary>
    internal ControlledApplicationLifetime Lifetime { get; }

    /// <summary>
    /// Gets the settings the endpoint got, <c>null</c> when ingest is disabled.
    /// </summary>
    internal IngestSettings? Settings { get; }

    /// <summary>
    /// Gets the endpoint.
    /// </summary>
    internal IngestEndpoint Endpoint { get; }

    /// <summary>
    /// Gets or sets the free space the probe reports, <c>null</c> for a volume that cannot be read.
    /// </summary>
    internal long? FreeBytes { get; set; } = 10L << 30;

    /// <summary>
    /// Gets or sets a function that answers the probe instead of <see cref="FreeBytes"/>, for a probe that waits for the test.
    /// </summary>
    internal Func<long?>? OnProbe { get; set; }

    /// <summary>
    /// Gets the directory of the database.
    /// </summary>
    internal string DirectoryPath => _directory?.Path ?? string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a rig with a writer that stores nothing.
    /// </summary>
    /// <param name="enabled">Whether the token and the agent ID are configured</param>
    /// <param name="minFree">The value of <c>ingest.min_free</c>; the check is off by default</param>
    /// <returns>The rig</returns>
    internal static IngestRig Create(bool enabled = true, string minFree = "0MiB")
    {
        return new IngestRig(null, null, enabled, minFree);
    }

    /// <summary>
    /// Creates a rig whose writer passes the batches to a real store in a temporary directory.
    /// </summary>
    /// <param name="cancellationToken">Cancels the opening of the store</param>
    /// <param name="minFree">The value of <c>ingest.min_free</c>; the check is off by default</param>
    /// <returns>The rig</returns>
    internal static async Task<IngestRig> CreateWithStoreAsync(CancellationToken cancellationToken, string minFree = "0MiB")
    {
        var directory = new TempDirectory();

        try
        {
            var store = await SqliteStore.OpenAsync(directory.Path, cancellationToken);

            return new IngestRig(directory, store, true, minFree);
        }
        catch
        {
            directory.Dispose();

            throw;
        }
    }

    /// <summary>
    /// Reads the rows of the records table of the database in a directory.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The rows ordered by sequence number</returns>
    internal static async Task<List<StoredRow>> ReadRowsAsync(string directory, CancellationToken cancellationToken)
    {
        var rows = new List<StoredRow>();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "vandox.db")};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT seq, origin, agent_id, boot_id, clock_offset_ns, received_at, kind, data FROM records ORDER BY seq";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StoredRow(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>
    /// Converts an instant to the nanoseconds since the Unix epoch the store writes.
    /// </summary>
    /// <param name="instant">The instant</param>
    /// <returns>The nanoseconds</returns>
    internal static long Nanoseconds(DateTimeOffset instant)
    {
        return (instant.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
    }

    /// <summary>
    /// Lets the other tasks run, so that a request that can make progress did before the test looks.
    /// </summary>
    /// <returns>A task that completes after the yields</returns>
    internal static async Task SettleAsync()
    {
        for (var index = 0; index < 2000; index++)
        {
            await Task.Yield();
        }
    }

    /// <summary>
    /// Runs a call to its answer.
    /// </summary>
    /// <param name="call">The call</param>
    /// <returns>A task that completes with the answer</returns>
    internal Task RunAsync(IngestCall call)
    {
        return Endpoint.HandleAsync(call.Context);
    }

    /// <summary>
    /// Posts a batch with the token and runs it to its answer.
    /// </summary>
    /// <param name="body">The compressed batch</param>
    /// <returns>The finished call</returns>
    internal async Task<IngestCall> PostAsync(byte[] body)
    {
        var call = IngestCall.Post(body);

        await RunAsync(call);

        return call;
    }

    /// <summary>
    /// Reads the rows of the records table of the real store.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The rows ordered by sequence number</returns>
    internal Task<List<StoredRow>> RowsAsync(CancellationToken cancellationToken)
    {
        return ReadRowsAsync(DirectoryPath, cancellationToken);
    }

    /// <summary>
    /// Runs a query on the database of the real store and returns the first column of the first row.
    /// </summary>
    /// <param name="sql">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The value, <c>null</c> when there is no row</returns>
    internal async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(DirectoryPath, "vandox.db")};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is DBNull ? null : value;
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Endpoint.Dispose();
        Lifetime.Dispose();
        Writer.Dispose();

        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        _directory?.Dispose();
    }

    #endregion // IAsyncDisposable
}