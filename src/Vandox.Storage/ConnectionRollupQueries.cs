using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The connection rollup reads: the states and top programs per step, the top addresses and the list of sources.
/// </summary>
internal static class ConnectionRollupQueries
{
    #region Constants

    /// <summary>
    /// The context of the errors of these reads.
    /// </summary>
    internal const string Context = "store: reading connection rollups";

    private const string SumValues = "MIN(MAX(COALESCE(sum(sum_value), 0), $low), $max)";

    private const string SourceParameter = "$source";

    private const string StartParameter = "$start";

    private const string EndParameter = "$end";

    private const string StepParameter = "$step";

    private const string MaxParameter = "$max";

    private const string LowParameter = "$low";

    private const string EarliestUnfoldedSql = "SELECT min(captured_at) FROM records INDEXED BY records_connection_fold WHERE kind = 'connection_snapshot' AND source = $source AND id > COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key = '" + ConnectionRollupSql.WatermarkKey + "'), 0)";

    private const string OldestFiveMinutesSql = "SELECT min(bucket) FROM connection_rollup_totals_5m WHERE source = $source";

    private const string OldestHourlySql = "SELECT min(bucket) FROM connection_rollup_totals_1h WHERE source = $source";

    private const string FiveMinuteRows = "connection_rollups_5m";

    private const string HourlyRows = "connection_rollups_1h";

    private const string SourcesSql = "SELECT source, max(bucket) FROM (SELECT source, bucket FROM connection_rollup_totals_5m WHERE bucket >= $from AND bucket < $to UNION ALL SELECT source, bucket FROM connection_rollup_totals_1h WHERE bucket >= $from AND bucket < $to) GROUP BY source ORDER BY source LIMIT $limit";

    private const string RangeFilter = "source = $source AND bucket >= $start AND bucket < $end";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the states and the top programs of a source per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static async Task<ConnectionRollupResult> ReadAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        RollupSteps.Validate(query.From, query.To, query.Step);

        if (query.From >= query.To)
        {
            return new ConnectionRollupResult();
        }

        var read = await PrepareAsync(connection, query, cancellationToken).ConfigureAwait(false);

        if (read.Totals is not { } totals)
        {
            return new ConnectionRollupResult
                   {
                       EarliestUnfolded = read.EarliestUnfolded,
                       StartsBeforeBuckets = read.StartsBeforeBuckets
                   };
        }

        var table = read.Hourly ? HourlyRows : FiveMinuteRows;
        var states = await ReadStatesAsync(connection, table, totals, read.Parameters, cancellationToken).ConfigureAwait(false);
        var ranked = await RankProgramsAsync(connection, table, read.Parameters, cancellationToken).ConfigureAwait(false);
        var programs = await ReadProgramPointsAsync(connection, table, ranked, totals, read.Parameters, cancellationToken).ConfigureAwait(false);

        return new ConnectionRollupResult
               {
                   Times = totals.Grid.Times(),
                   States = states,
                   Programs = programs,
                   Others = RollupSteps.Others(totals.ProcessSums, totals.Named, totals.Snapshots),
                   Incomplete = ToCounts(totals.Incomplete),
                   EarliestUnfolded = read.EarliestUnfolded,
                   StartsBeforeBuckets = read.StartsBeforeBuckets
               };
    }

    /// <summary>
    /// Reads the top addresses of a source with the figures per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static async Task<ConnectionAddressResult> ReadAddressesAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        RollupSteps.Validate(query.From, query.To, query.Step);

        if (query.From >= query.To)
        {
            return new ConnectionAddressResult();
        }

        var read = await PrepareAsync(connection, query, cancellationToken).ConfigureAwait(false);

        if (read.Totals is not { } totals)
        {
            return new ConnectionAddressResult
                   {
                       EarliestUnfolded = read.EarliestUnfolded,
                       StartsBeforeBuckets = read.StartsBeforeBuckets
                   };
        }

        var table = read.Hourly ? HourlyRows : FiveMinuteRows;
        var rangeSnapshots = totals.Snapshots.Sum();
        var sql = $"SELECT name, {SumValues} AS total, max(max_value) FROM {table} WHERE kind = 2 AND {RangeFilter} GROUP BY name ORDER BY total DESC, name LIMIT $count";
        var used = new Dictionary<string, object>(read.Parameters)
                   {
                       ["$count"] = (long)StorageLimits.MaxConnectionRollupAddresses
                   };
        var rows = await ReaderQuery.ReadAsync(connection, sql, used, reader => (Name: reader.GetString(0), Total: reader.GetDouble(1), Maximum: reader.GetDouble(2)), Context, cancellationToken).ConfigureAwait(false);
        var distinct = await RollupSteps.ReadNullableAsync(connection, $"SELECT count(DISTINCT name) FROM {table} WHERE kind = 2 AND {RangeFilter}", read.Parameters, Context, cancellationToken).ConfigureAwait(false);

        return new ConnectionAddressResult
               {
                   Times = totals.Grid.Times(),
                   Gaps = [.. totals.Snapshots.Select(snapshots => snapshots == 0)],
                   Addresses = [
                                   .. rows.Select(row => new ConnectionAddress
                                                         {
                                                             Address = row.Name,
                                                             Average = row.Total / rangeSnapshots,
                                                             Maximum = row.Maximum
                                                         })
                               ],
                   Further = Math.Max((distinct ?? 0) - rows.Count, 0),
                   Incomplete = ToCounts(totals.Incomplete),
                   Capped = ToCounts(totals.Capped),
                   EarliestUnfolded = read.EarliestUnfolded,
                   StartsBeforeBuckets = read.StartsBeforeBuckets
               };
    }

    /// <summary>
    /// Reads the sources that have connection rollups in a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sources</returns>
    internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        ProgramRollupQueries.ValidateSources(query);

        return RollupSteps.ReadSourceListAsync(connection, query, SourcesSql, Context, cancellationToken);
    }

    /// <summary>
    /// Reads what both reads share: the steps, the completeness and the totals per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated, non-empty query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the shared part</returns>
    private static async Task<ConnectionReadContext> PrepareAsync(SqliteConnection connection, ConnectionRollupQuery query, CancellationToken cancellationToken)
    {
        var grid = ProgramStepGrid.Create(query.From, query.To, query.Step);
        var hourly = query.Step >= TimeSpan.FromHours(1);
        var parameters = new Dictionary<string, object>
                         {
                             [SourceParameter] = query.Source,
                             [StartParameter] = grid.StartNanoseconds,
                             [EndParameter] = grid.EndNanoseconds,
                             [StepParameter] = grid.StepNanoseconds,
                             [MaxParameter] = double.MaxValue,
                             [LowParameter] = -double.MaxValue
                         };
        var unfolded = await RollupSteps.ReadNullableAsync(connection, EarliestUnfoldedSql, parameters, Context, cancellationToken).ConfigureAwait(false);
        var oldest = await RollupSteps.ReadNullableAsync(connection, hourly ? OldestHourlySql : OldestFiveMinutesSql, parameters, Context, cancellationToken).ConfigureAwait(false);
        var totals = await ReadTotalsAsync(connection, hourly, grid, parameters, cancellationToken).ConfigureAwait(false);

        return new ConnectionReadContext(hourly,
                                         parameters,
                                         totals,
                                         unfolded is { } captured ? StorageTime.FromNanoseconds(captured) : null,
                                         oldest is { } first && StorageTime.ToNanoseconds(query.From) < first);
    }

    /// <summary>
    /// Reads the totals of the steps of the range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="hourly">Whether the hourly tier is read</param>
    /// <param name="grid">The steps</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the totals, or <c>null</c> when the range holds no total</returns>
    private static async Task<ConnectionStepTotals?> ReadTotalsAsync(SqliteConnection connection, bool hourly, ProgramStepGrid grid, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var table = hourly ? "connection_rollup_totals_1h" : "connection_rollup_totals_5m";
        var sql = $"SELECT {RollupSteps.StepIndex} AS step, sum(snapshots), sum(incomplete), sum(capped), {RollupSteps.Clamp("sum(process_sum)")} FROM {table} WHERE {RangeFilter} GROUP BY step";
        var rows = await ReaderQuery.ReadAsync(connection,
                                               sql,
                                               parameters,
                                               reader => (Index: reader.GetInt64(0), Snapshots: reader.GetInt64(1), Incomplete: reader.GetInt64(2), Capped: reader.GetInt64(3), ProcessSum: reader.GetDouble(4)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (rows.Count == 0)
        {
            return null;
        }

        var totals = new ConnectionStepTotals(grid);

        foreach (var row in rows)
        {
            var position = grid.PositionOf(row.Index);

            totals.Snapshots[position] = row.Snapshots;
            totals.Incomplete[position] = row.Incomplete;
            totals.Capped[position] = row.Capped;
            totals.ProcessSums[position] = row.ProcessSum;
        }

        return totals;
    }

    /// <summary>
    /// Reads every state of the range with its points per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="table">The row table of the tier</param>
    /// <param name="totals">The totals per step</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the states in ordinal name order</returns>
    private static async Task<List<ConnectionRollupLine>> ReadStatesAsync(SqliteConnection connection, string table, ConnectionStepTotals totals, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var sql = $"SELECT name, {RollupSteps.StepIndex} AS step, {SumValues}, sum(seen), min(min_value), max(max_value) FROM {table} WHERE kind = 0 AND {RangeFilter} GROUP BY name, step ORDER BY name, step";
        var rows = await ReadConnectionPointRowsAsync(connection, sql, parameters, cancellationToken).ConfigureAwait(false);
        var lines = new Dictionary<string, ProgramRollupPoint?[]>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            Place(lines, totals, row, false);
        }

        return [
                   .. lines.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ConnectionRollupLine
                                                                                             {
                                                                                                 Name = pair.Key,
                                                                                                 Points = pair.Value
                                                                                             })
               ];
    }

    /// <summary>
    /// Ranks the programs of the range by their sum, the highest first, a tie by name.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="table">The row table of the tier</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the names of the top programs</returns>
    private static async Task<IReadOnlyList<string>> RankProgramsAsync(SqliteConnection connection, string table, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var sql = $"SELECT name, {SumValues} AS total FROM {table} WHERE kind = 1 AND {RangeFilter} GROUP BY name ORDER BY total DESC, name LIMIT $count";
        var used = new Dictionary<string, object>(parameters)
                   {
                       ["$count"] = (long)StorageLimits.MaxConnectionRollupPrograms
                   };

        return await ReaderQuery.ReadAsync(connection, sql, used, reader => reader.GetString(0), Context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the points of the named programs per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="table">The row table of the tier</param>
    /// <param name="ranked">The named programs</param>
    /// <param name="totals">The totals per step, whose named sums are filled</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the lines in rank order</returns>
    private static async Task<List<ConnectionRollupLine>> ReadProgramPointsAsync(SqliteConnection connection,
                                                                                 string table,
                                                                                 IReadOnlyList<string> ranked,
                                                                                 ConnectionStepTotals totals,
                                                                                 Dictionary<string, object> parameters,
                                                                                 CancellationToken cancellationToken)
    {
        var lines = new Dictionary<string, ProgramRollupPoint?[]>(StringComparer.Ordinal);

        foreach (var name in ranked)
        {
            lines[name] = Empty(totals);
        }

        if (ranked.Count > 0)
        {
            var sql = $"SELECT name, {RollupSteps.StepIndex} AS step, {SumValues}, sum(seen), min(min_value), max(max_value) FROM {table} WHERE kind = 1 AND {RangeFilter} AND name IN (SELECT value FROM json_each($names)) GROUP BY name, step";
            var used = new Dictionary<string, object>(parameters)
                       {
                           ["$names"] = JsonSerializer.Serialize(ranked)
                       };

            foreach (var row in await ReadConnectionPointRowsAsync(connection, sql, used, cancellationToken).ConfigureAwait(false))
            {
                Place(lines, totals, row, true);
            }
        }

        return [
                   .. ranked.Select(name => new ConnectionRollupLine
                                            {
                                                Name = name,
                                                Points = lines[name]
                                            })
               ];
    }

    /// <summary>
    /// Reads the grouped rows of a point query.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the rows</returns>
    private static async Task<IReadOnlyList<ConnectionPointRow>> ReadConnectionPointRowsAsync(SqliteConnection connection, string sql, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection,
                                               sql,
                                               parameters,
                                               reader => new ConnectionPointRow(reader.GetString(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetInt64(3), reader.GetDouble(4), reader.GetDouble(5)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return rows;
    }

    /// <summary>
    /// Puts the point of a row into the line of its item.
    /// </summary>
    /// <param name="lines">The lines by name; a new item starts with zero points in the steps that hold snapshots</param>
    /// <param name="totals">The totals per step</param>
    /// <param name="row">The row</param>
    /// <param name="named">Whether the row is of a named program, whose sum counts as named</param>
    private static void Place(Dictionary<string, ProgramRollupPoint?[]> lines, ConnectionStepTotals totals, ConnectionPointRow row, bool named)
    {
        var position = totals.Grid.PositionOf(row.Index);

        if (totals.Snapshots[position] == 0)
        {
            return;
        }

        if (lines.TryGetValue(row.Name, out var line) == false)
        {
            line = Empty(totals);
            lines[row.Name] = line;
        }

        line[position] = RollupSteps.Point(row.Sum, row.Seen, row.Minimum, row.Maximum, totals.Snapshots[position]);

        if (named)
        {
            totals.Named[position] = TimeSeriesQueries.ClampSum(totals.Named[position] + row.Sum);
        }
    }

    /// <summary>
    /// Creates the points of an item no snapshot names: zero in a step with snapshots, a gap in a step without.
    /// </summary>
    /// <param name="totals">The totals per step</param>
    /// <returns>The points</returns>
    private static ProgramRollupPoint?[] Empty(ConnectionStepTotals totals)
    {
        return [.. totals.Snapshots.Select(snapshots => snapshots > 0 ? new ProgramRollupPoint(0, 0, 0) : (ProgramRollupPoint?)null)];
    }

    /// <summary>
    /// Converts counts to the range of an integer.
    /// </summary>
    /// <param name="counts">The counts</param>
    /// <returns>The counts, none above the largest integer</returns>
    private static List<int> ToCounts(long[] counts)
    {
        return [.. counts.Select(count => (int)Math.Min(count, int.MaxValue))];
    }

    #endregion // Methods
}