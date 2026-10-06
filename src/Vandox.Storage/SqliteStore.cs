using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The backend's SQLite database in WAL mode: one writer connection that runs one batch per immediate transaction, and a
/// bounded number of query-only readers, so a committed batch is durable and reads (and the health check) do not wait for
/// a write.
/// </summary>
public sealed class SqliteStore : IRecordWriter, IRecordReader, ILogSearcher, IImportTracker, IAsyncDisposable
{
    #region Constants

    private const int MaxReaders = 4;
    private const string CommonPragmas = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=1; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";

    #endregion // Constants

    #region Fields

    private readonly string _path;
    private readonly SqliteConnection _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _readers = new(MaxReaders, MaxReaders);
    private bool _disposed;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteStore"/> class.
    /// </summary>
    /// <param name="path">The path of the database file</param>
    /// <param name="writer">The open writer connection</param>
    /// <param name="created">Whether the file was created</param>
    private SqliteStore(string path, SqliteConnection writer, bool created)
    {
        _path = path;
        _writer = writer;
        Created = created;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether opening created the database file.
    /// </summary>
    public bool Created { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Opens the database <see cref="StorageLimits.FileName"/> in the existing directory, creating the file (mode 0600) when
    /// it does not exist, in WAL mode, and migrates its schema to <see cref="StorageLimits.SchemaVersion"/>.
    /// </summary>
    /// <param name="directory">The existing storage directory; it is never created, so a missing mount is not hidden</param>
    /// <param name="cancellationToken">Cancels the open</param>
    /// <returns>A task that returns the open store</returns>
    /// <exception cref="StoreException">The directory or the file is unusable, or the schema cannot be migrated</exception>
    public static async Task<SqliteStore> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        if (Directory.Exists(directory))
        {
            return await OpenInAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        throw new StoreException($"store: checking storage directory: {directory} is not a directory");
    }

    /// <summary>
    /// Reads the schema version and fails when the database cannot be read.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that completes when the database answered</returns>
    /// <exception cref="StoreException">The database cannot be read</exception>
    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);

        await SchemaMigrator.ReadVersionAsync(lease.Connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the database file in a directory that exists.
    /// </summary>
    /// <param name="directory">The storage directory</param>
    /// <param name="cancellationToken">Cancels the open</param>
    /// <returns>A task that returns the open store</returns>
    private static async Task<SqliteStore> OpenInAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, StorageLimits.FileName);
        var created = DatabaseFile.Prepare(path);
        var writer = new SqliteConnection(ConnectionString(path));

        try
        {
            await writer.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(writer, CommonPragmas, cancellationToken).ConfigureAwait(false);
            await RequireWalAsync(writer, cancellationToken).ConfigureAwait(false);
            await SchemaMigrator.MigrateAsync(writer, cancellationToken).ConfigureAwait(false);

            var store = new SqliteStore(path, writer, created);

            await store.CheckReaderAsync(cancellationToken).ConfigureAwait(false);

            return store;
        }
        catch (Exception exception) when (exception is SqliteException)
        {
            await writer.DisposeAsync().ConfigureAwait(false);

            throw new StoreException($"store: opening database: {exception.Message}", exception);
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Creates the connection string of the database file. The path goes in as a file name, never as a URI, so characters
    /// such as <c>?</c>, <c>#</c> and <c>%</c> cannot add parameters or cut the path.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns>The connection string</returns>
    private static string ConnectionString(string path)
    {
        return new SqliteConnectionStringBuilder
               {
                   DataSource = path,
                   Mode = SqliteOpenMode.ReadWrite,
                   Pooling = false
               }.ToString();
    }

    /// <summary>
    /// Runs one or more statements.
    /// </summary>
    /// <param name="connection">The connection</param>
    /// <param name="sql">The statements</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that completes when the statements ran</returns>
    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies WAL mode.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that completes when the mode is WAL</returns>
    private static async Task RequireWalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA journal_mode";

        var mode = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;

        if (mode != "wal")
        {
            throw new StoreException($"store: journal mode is \"{mode}\", want wal (the file system must support shared memory)");
        }
    }

    /// <summary>
    /// Checks that the readers read the migrated database.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that completes when a reader read the schema version</returns>
    private async Task CheckReaderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Opens a query-only reader connection, waiting for one of the <see cref="MaxReaders"/> slots.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns the lease</returns>
    private async Task<ReaderLease> LeaseReaderAsync(CancellationToken cancellationToken)
    {
        await _readers.WaitAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(ConnectionString(_path));

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, $"{CommonPragmas} PRAGMA query_only=1;", cancellationToken).ConfigureAwait(false);

            return new ReaderLease(connection, _readers);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _readers.Release();

            throw;
        }
    }

    /// <summary>
    /// Runs a query on a reader connection and maps the rows.
    /// </summary>
    /// <param name="sql">The SQL</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that returns the records</returns>
    private async Task<IReadOnlyList<StoredRecord>> QueryAsync(string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);
            using var command = lease.Connection.CreateCommand();

            command.CommandText = sql;

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var records = new List<StoredRecord>();

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(RecordQueries.Read(reader));
            }

            return records;
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: reading records: {exception.Message}", exception);
        }
    }

    #endregion // Methods

    #region IRecordWriter

    /// <inheritdoc />
    public async Task<WriteResult> WriteBatchAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        BatchValidator.Validate(batch);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var transaction = (SqliteTransaction)await _writer.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            using var writer = new BatchWriter(_writer, transaction);

            if (batch.Import is not null)
            {
                await ImportRepository.AdvanceAsync(_writer, transaction, batch.Import, batch.Records.Count, batch.ReceivedAt, cancellationToken).ConfigureAwait(false);
            }

            var stored = 0;

            foreach (var record in batch.Records)
            {
                if (await writer.WriteAsync(batch, record, cancellationToken).ConfigureAwait(false))
                {
                    stored++;
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new WriteResult(stored, batch.Records.Count - stored);
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: writing batch: {exception.Message}", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion // IRecordWriter

    #region IRecordReader

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        RecordQueries.Validate(query);

        var (sql, parameters) = RecordQueries.Build(query);

        return QueryAsync(sql, parameters, cancellationToken);
    }

    #endregion // IRecordReader

    #region ILogSearcher

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> SearchLogsAsync(LogSearch search, CancellationToken cancellationToken)
    {
        RecordQueries.ValidateWindow(search.From, search.To, search.Limit);

        var (sql, parameters) = RecordQueries.Build(search);

        return QueryAsync(sql, parameters, cancellationToken);
    }

    #endregion // ILogSearcher

    #region IImportTracker

    /// <inheritdoc />
    public async Task<ImportFile> BeginImportAsync(ImportFileStart start, CancellationToken cancellationToken)
    {
        ImportRepository.Validate(start);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ImportRepository.BeginAsync(_writer, start, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: beginning import: {exception.Message}", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    #endregion // IImportTracker

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _writer.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        _readers.Dispose();
    }

    #endregion // IAsyncDisposable
}