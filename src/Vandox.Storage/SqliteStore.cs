using Microsoft.Data.Sqlite;

using Vandox.Core.Incidents;
using Vandox.Core.Templates;

namespace Vandox.Storage;

/// <summary>
/// The backend's SQLite database in WAL mode: one writer connection that runs one batch per immediate transaction, and a
/// bounded number of query-only readers, so a committed batch is durable and reads (and the health check) do not wait for
/// a write.
/// </summary>
public sealed class SqliteStore : IRecordWriter, IRecordReader, ILogSearcher, ILogTemplateReader, IIncidentStore, IImportTracker, IAsyncDisposable
{
    #region Constants

    private const int MaxReaders = 4;

    /// <summary>
    /// The operation named in the error of a record read.
    /// </summary>
    private const string RecordsContext = "store: reading records";

    /// <summary>
    /// The operation named in the error of a template read.
    /// </summary>
    private const string TemplatesContext = "store: reading templates";

    private const string CommonPragmas = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=1; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";

    // The writer connection holds up to 16 MiB of pages (the default is 2 MiB), so a large transaction does not spill pages to the
    // file system before the commit; the readers keep the default (record 0083).
    private const string WriterPragmas = CommonPragmas + " PRAGMA cache_size=-16384;";

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
        try
        {
            await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);

            await SchemaMigrator.ReadVersionAsync(lease.Connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw ReaderQuery.Translate(exception, "store: reading schema version", cancellationToken);
        }
    }

    /// <summary>
    /// Opens a query-only reader connection, waiting for one of the <see cref="MaxReaders"/> slots.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait</param>
    /// <returns>A task that returns the lease</returns>
    internal async Task<ReaderLease> LeaseReaderAsync(CancellationToken cancellationToken)
    {
        await _readers.WaitAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(ConnectionString(_path));

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, $"{CommonPragmas} PRAGMA query_only=1;", cancellationToken).ConfigureAwait(false);

            ReaderQuery.Attach(connection, cancellationToken);

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
    /// Rebuilds the template statistics from the stored log lines: empties the three tables, writes the run marker and fixes the
    /// high-water record ID in one transaction, then counts the lines up to it in batches of at most <paramref name="batchLines"/>,
    /// one transaction each, and removes the marker; a run whose marker was replaced by another rebuild fails.
    /// </summary>
    /// <param name="batchLines">The most log lines one transaction counts, 1 to <see cref="StorageLimits.MaxBatchRecords"/></param>
    /// <param name="afterBatch">Called outside the writer lock after each committed batch with the lines counted so far; may be <c>null</c></param>
    /// <param name="cancellationToken">Cancels the rebuild</param>
    /// <returns>A task that returns the result of the rebuild</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchLines"/> is outside 1 to <see cref="StorageLimits.MaxBatchRecords"/></exception>
    /// <exception cref="StoreException">The database fails, or another rebuild superseded this one</exception>
    public async Task<TemplateRebuildResult> RebuildTemplatesAsync(int batchLines, Func<long, CancellationToken, Task>? afterBatch, CancellationToken cancellationToken)
    {
        if (batchLines < 1 || batchLines > StorageLimits.MaxBatchRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(batchLines), batchLines, $"batchLines must be 1 to {StorageLimits.MaxBatchRecords}");
        }

        try
        {
            var marker = TemplateRebuilder.NewMarker();
            var highId = await RunWriteAsync(async (transaction, token) => await TemplateRebuilder.ClearAsync(_writer, transaction, marker, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            var after = 0L;
            var lines = 0L;
            var batches = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (lastId, counted) = await RunWriteAsync(async (transaction, token) =>
                                                            {
                                                                await RequireMarkerAsync(transaction, marker, token).ConfigureAwait(false);

                                                                return await TemplateRebuilder.CountBatchAsync(_writer, transaction, after, highId, batchLines, token).ConfigureAwait(false);
                                                            },
                                                            cancellationToken).ConfigureAwait(false);

                if (counted == 0)
                {
                    break;
                }

                after = lastId;
                lines += counted;
                batches++;

                if (afterBatch is not null)
                {
                    await afterBatch(lines, cancellationToken).ConfigureAwait(false);
                }

                if (counted < batchLines)
                {
                    break;
                }
            }

            var templates = await RunWriteAsync(async (transaction, token) =>
                                                {
                                                    await RequireMarkerAsync(transaction, marker, token).ConfigureAwait(false);

                                                    return await TemplateRebuilder.FinishAsync(_writer, transaction, token).ConfigureAwait(false);
                                                },
                                                cancellationToken).ConfigureAwait(false);

            return new TemplateRebuildResult
                   {
                       Lines = lines,
                       Batches = batches,
                       Templates = templates
                   };
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: rebuilding templates: {exception.Message}", exception);
        }
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
            await ExecuteAsync(writer, WriterPragmas, cancellationToken).ConfigureAwait(false);
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
    /// Runs a query on a reader connection and maps the rows.
    /// </summary>
    /// <typeparam name="T">The type of the result rows</typeparam>
    /// <param name="sql">The SQL</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="map">Maps the current row</param>
    /// <param name="context">The operation named in an error message</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that returns the rows</returns>
    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Dictionary<string, object> parameters, Func<SqliteDataReader, T> map, string context, CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);

            return await ReaderQuery.ReadAsync(lease.Connection, sql, parameters, map, context, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw ReaderQuery.Translate(exception, context, cancellationToken);
        }
    }

    /// <summary>
    /// Runs a template lookup and returns its first row.
    /// </summary>
    /// <param name="sql">The SQL</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the query</param>
    /// <returns>A task that returns the template, or <c>null</c> when there is none</returns>
    private async Task<LogTemplate?> FindFirstAsync(string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(sql, parameters, TemplateQueries.ReadTemplate, TemplatesContext, cancellationToken).ConfigureAwait(false);

        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>
    /// Runs an action in one committed writer transaction under the writer lock.
    /// </summary>
    /// <typeparam name="TResult">The type of the result</typeparam>
    /// <param name="action">The action</param>
    /// <param name="cancellationToken">Cancels the transaction</param>
    /// <returns>A task that returns the result of the action</returns>
    private async Task<TResult> RunWriteAsync<TResult>(Func<SqliteTransaction, CancellationToken, Task<TResult>> action, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var transaction = (SqliteTransaction)await _writer.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var result = await action(transaction, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Fails when the run marker of a template rebuild was replaced or removed.
    /// </summary>
    /// <param name="transaction">The open transaction</param>
    /// <param name="marker">The marker of this run</param>
    /// <param name="cancellationToken">Cancels the check</param>
    /// <returns>A task that completes when the marker is this run's</returns>
    /// <exception cref="StoreException">Another rebuild superseded this one</exception>
    private async Task RequireMarkerAsync(SqliteTransaction transaction, string marker, CancellationToken cancellationToken)
    {
        if (await TemplateRebuilder.HoldsMarkerAsync(_writer, transaction, marker, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        throw new StoreException("store: rebuilding templates: superseded by another rebuild");
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

            var stored = batch.Records.Count(record => writer.Write(batch, record, cancellationToken));

            writer.WriteTemplates(cancellationToken);

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

        return QueryAsync(sql, parameters, RecordQueries.Read, RecordsContext, cancellationToken);
    }

    #endregion // IRecordReader

    #region ILogSearcher

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredRecord>> SearchLogsAsync(LogSearch search, CancellationToken cancellationToken)
    {
        RecordQueries.ValidateWindow(search.From, search.To, search.Limit);

        var (sql, parameters) = RecordQueries.Build(search);

        return QueryAsync(sql, parameters, RecordQueries.Read, RecordsContext, cancellationToken);
    }

    #endregion // ILogSearcher

    #region ILogTemplateReader

    /// <inheritdoc />
    public Task<LogTemplate?> FindTemplateAsync(string source, string program, string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(program);
        TemplateQueries.ValidateMessage(message);

        var (sql, parameters) = TemplateQueries.BuildFind(source, program, LogTemplateNormalizer.Normalize(message));

        return FindFirstAsync(sql, parameters, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken)
    {
        TemplateQueries.Validate(query);

        var (sql, parameters) = TemplateQueries.Build(query);

        return QueryAsync(sql, parameters, TemplateQueries.ReadHour, TemplatesContext, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LogTemplate>> NewTemplatesAsync(NewTemplateQuery query, CancellationToken cancellationToken)
    {
        TemplateQueries.Validate(query);

        var (sql, parameters) = TemplateQueries.Build(query);

        return QueryAsync(sql, parameters, TemplateQueries.ReadTemplate, TemplatesContext, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateSpike>> TemplateSpikesAsync(TemplateSpikeQuery query, CancellationToken cancellationToken)
    {
        TemplateQueries.Validate(query);

        var (sql, parameters) = TemplateQueries.Build(query);

        return QueryAsync(sql, parameters, TemplateQueries.ReadSpike, TemplatesContext, cancellationToken);
    }

    #endregion // ILogTemplateReader

    #region IIncidentStore

    /// <inheritdoc />
    public async Task<IncidentSaveResult> SaveIncidentAsync(Incident incident, DateTimeOffset savedAt, CancellationToken cancellationToken)
    {
        var data = IncidentRepository.Validate(incident, savedAt);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var transaction = (SqliteTransaction)await _writer.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            var result = await IncidentRepository.SaveAsync(_writer, transaction, incident, data, savedAt, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: saving incident: {exception.Message}", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredIncident>> IncidentsAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.ValidateWindow(query.From, query.To, query.Limit);

        var (sql, parameters) = IncidentRepository.Build(query);

        return await QueryAsync(sql, parameters, IncidentRepository.Read, "store: reading incidents", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountIncidentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);
            using var command = lease.Connection.CreateCommand();

            command.CommandText = "SELECT count(*) FROM incidents";

            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception)
        {
            throw ReaderQuery.Translate(exception, "store: reading incidents", cancellationToken);
        }
    }

    #endregion // IIncidentStore

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportBase>> FindImportBasesAsync(byte[] headSha256, string sourceType, CancellationToken cancellationToken)
    {
        ImportRepository.ValidateLookup(headSha256, sourceType);

        try
        {
            await using var lease = await LeaseReaderAsync(cancellationToken).ConfigureAwait(false);

            return await ImportRepository.FindBasesAsync(lease.Connection, headSha256, sourceType, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw ReaderQuery.Translate(exception, "store: finding import bases", cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task AddLineHashesAsync(ImportLineHashes hashes, CancellationToken cancellationToken)
    {
        ImportRepository.ValidateLineHashes(hashes);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ImportRepository.AddLineHashesAsync(_writer, hashes, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new StoreException($"store: adding line hashes: {exception.Message}", exception);
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