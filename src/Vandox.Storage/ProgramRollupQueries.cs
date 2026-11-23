using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The per-program rollup reads: the top programs of a source per step and the list of sources.
/// </summary>
internal static class ProgramRollupQueries
{
    #region Constants

    /// <summary>
    /// The context of the errors of these reads.
    /// </summary>
    internal const string Context = "store: reading program rollups";

    private const string SourceParameter = "$source";

    private const string StartParameter = "$start";

    private const string EndParameter = "$end";

    private const string StepParameter = "$step";

    private const string MaxParameter = "$max";

    private const string LowParameter = "$low";

    private const string EarliestUnfoldedSql = "SELECT min(captured_at) FROM records INDEXED BY records_process_fold WHERE kind = 'process_snapshot' AND source = $source AND id > COALESCE((SELECT CAST(value AS INTEGER) FROM meta WHERE key = '" + RetentionQueries.ProgramRollupWatermarkKey + "'), 0)";

    private const string OldestFiveMinutesSql = "SELECT min(bucket) FROM program_rollup_totals_5m WHERE source = $source";

    private const string OldestHourlySql = "SELECT min(bucket) FROM program_rollup_totals_1h WHERE source = $source";

    private const string FiveMinutePrograms = "program_rollups_5m";

    private const string HourlyPrograms = "program_rollups_1h";

    private const string SourcesSql = "SELECT source, max(bucket) FROM (SELECT source, bucket FROM program_rollup_totals_5m WHERE bucket >= $from AND bucket < $to UNION ALL SELECT source, bucket FROM program_rollup_totals_1h WHERE bucket >= $from AND bucket < $to) GROUP BY source ORDER BY source LIMIT $limit";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Validates a per-program rollup query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query is invalid</exception>
    internal static void Validate(ProgramRollupQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(query.Count >= 1 && query.Count <= StorageLimits.MaxProgramRollupCount, $"count must be 1 to {StorageLimits.MaxProgramRollupCount}");
        RecordQueries.Require(Enum.IsDefined(query.Measure), "unknown measure");
        RollupSteps.Validate(query.From, query.To, query.Step);
    }

    /// <summary>
    /// Reads the top programs of a source per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result</returns>
    internal static async Task<ProgramRollupResult> ReadAsync(SqliteConnection connection, ProgramRollupQuery query, CancellationToken cancellationToken)
    {
        Validate(query);

        if (query.From >= query.To)
        {
            return new ProgramRollupResult();
        }

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
        var startsBefore = oldest is { } first && StorageTime.ToNanoseconds(query.From) < first;
        var totals = await ReadTotalsAsync(connection, hourly, grid, parameters, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? earliestUnfolded = unfolded is { } captured ? StorageTime.FromNanoseconds(captured) : null;

        if (totals is null)
        {
            return new ProgramRollupResult
                   {
                       EarliestUnfolded = earliestUnfolded,
                       StartsBeforeBuckets = startsBefore
                   };
        }

        var key = query.Measure == ProgramMeasure.Memory && totals.AllPss() ? ProgramMemoryKey.Pss : ProgramMemoryKey.Rss;
        var sums = totals.Sums(query.Measure, key);
        var column = Column(query.Measure, key);
        var ranked = await RankAsync(connection, hourly, column, query.Count, parameters, cancellationToken).ConfigureAwait(false);
        var points = await ReadPointsAsync(connection, hourly ? HourlyPrograms : FiveMinutePrograms, column, ranked, totals, parameters, cancellationToken).ConfigureAwait(false);
        var lines = ranked.Select(entry => new ProgramRollupLine
                                           {
                                               Program = entry.Program,
                                               Points = points[entry.Program]
                                           }).ToList();

        return new ProgramRollupResult
               {
                   Times = grid.Times(),
                   Programs = lines,
                   Others = RollupSteps.Others(sums, totals.Named, totals.Snapshots),
                   Incomplete = [.. totals.Incomplete.Select(count => (int)Math.Min(count, int.MaxValue))],
                   MemoryKey = query.Measure == ProgramMeasure.Memory ? key : null,
                   EarliestUnfolded = earliestUnfolded,
                   StartsBeforeBuckets = startsBefore
               };
    }

    /// <summary>
    /// Validates a program source query.
    /// </summary>
    /// <param name="query">The query</param>
    /// <exception cref="InvalidQueryException">The query is invalid</exception>
    internal static void ValidateSources(ProgramSourceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        RecordQueries.Require(StorageTime.InStorableRange(query.From) && StorageTime.InStorableRange(query.To), "from and to must be within the storable range");
    }

    /// <summary>
    /// Reads the sources that have program rollups in a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sources</returns>
    internal static Task<ProgramSourceList> ReadSourcesAsync(SqliteConnection connection, ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        ValidateSources(query);

        return RollupSteps.ReadSourceListAsync(connection, query, SourcesSql, Context, cancellationToken);
    }

    /// <summary>
    /// Returns the column prefix of the measure that ranks the programs.
    /// </summary>
    /// <param name="measure">The measure</param>
    /// <param name="key">The memory key</param>
    /// <returns>The column prefix</returns>
    private static string Column(ProgramMeasure measure, ProgramMemoryKey key)
    {
        if (measure == ProgramMeasure.Cpu)
        {
            return "cpu";
        }

        return key == ProgramMemoryKey.Pss ? "pss" : "rss";
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
    private static async Task<ProgramStepTotals?> ReadTotalsAsync(SqliteConnection connection, bool hourly, ProgramStepGrid grid, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var table = hourly ? "program_rollup_totals_1h" : "program_rollup_totals_5m";
        var sql = $"SELECT {RollupSteps.StepIndex} AS step, sum(snapshots), sum(pss_snapshots), sum(incomplete), {RollupSteps.Clamp("sum(rss_sum)")}, {RollupSteps.Clamp("sum(pss_sum)")}, {RollupSteps.Clamp("sum(cpu_sum)")} FROM {table} WHERE source = $source AND bucket >= $start AND bucket < $end GROUP BY step";
        var rows = await ReaderQuery.ReadAsync(connection,
                                               sql,
                                               parameters,
                                               reader => (Index: reader.GetInt64(0), Snapshots: reader.GetInt64(1), Pss: reader.GetInt64(2), Incomplete: reader.GetInt64(3), Rss: reader.GetDouble(4), PssSum: reader.GetDouble(5), Cpu: reader.GetDouble(6)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var totals = new ProgramStepTotals(grid);
        var any = false;

        foreach (var row in rows)
        {
            var position = grid.PositionOf(row.Index);

            if (position < 0)
            {
                continue;
            }

            any = true;
            totals.Snapshots[position] = row.Snapshots;
            totals.PssSnapshots[position] = row.Pss;
            totals.Incomplete[position] = row.Incomplete;
            totals.Rss[position] = row.Rss;
            totals.Pss[position] = row.PssSum;
            totals.Cpu[position] = row.Cpu;
        }

        return any ? totals : null;
    }

    /// <summary>
    /// Ranks the programs of the range by their sum of the measure, the highest first, a tie by name.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="hourly">Whether the hourly tier is read</param>
    /// <param name="column">The column prefix of the measure</param>
    /// <param name="count">The number of programs</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the program names with their clamped sums</returns>
    private static async Task<IReadOnlyList<(string Program, double Total)>> RankAsync(SqliteConnection connection, bool hourly, string column, int count, Dictionary<string, object> parameters, CancellationToken cancellationToken)
    {
        var table = hourly ? HourlyPrograms : FiveMinutePrograms;
        var sql = $"SELECT program, {RollupSteps.Clamp($"sum({column}_sum)")} AS total FROM {table} WHERE source = $source AND bucket >= $start AND bucket < $end GROUP BY program ORDER BY total DESC, program LIMIT $count";
        var used = new Dictionary<string, object>(parameters)
                   {
                       ["$count"] = (long)count
                   };
        var rows = await ReaderQuery.ReadAsync(connection, sql, used, reader => (Program: reader.GetString(0), Total: reader.GetDouble(1)), Context, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return rows;
    }

    /// <summary>
    /// Reads the points of the named programs per step.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="table">The program table of the tier</param>
    /// <param name="column">The column prefix of the measure</param>
    /// <param name="ranked">The named programs</param>
    /// <param name="totals">The totals per step, whose named sums are filled</param>
    /// <param name="parameters">The parameters</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the points of each program; a step without snapshots is a gap, a program the snapshots of a step do not name reads 0</returns>
    private static async Task<Dictionary<string, ProgramRollupPoint?[]>> ReadPointsAsync(SqliteConnection connection,
                                                                                         string table,
                                                                                         string column,
                                                                                         IReadOnlyList<(string Program, double Total)> ranked,
                                                                                         ProgramStepTotals totals,
                                                                                         Dictionary<string, object> parameters,
                                                                                         CancellationToken cancellationToken)
    {
        var points = new Dictionary<string, ProgramRollupPoint?[]>(StringComparer.Ordinal);

        foreach (var (program, _) in ranked)
        {
            points[program] = [.. totals.Snapshots.Select(snapshots => snapshots > 0 ? new ProgramRollupPoint(0, 0, 0) : (ProgramRollupPoint?)null)];
        }

        if (ranked.Count == 0)
        {
            return points;
        }

        var sql = $"SELECT program, {RollupSteps.StepIndex} AS step, {RollupSteps.Clamp($"sum({column}_sum)")}, sum(seen), min({column}_min), max({column}_max) FROM {table} WHERE source = $source AND bucket >= $start AND bucket < $end AND program IN (SELECT value FROM json_each($names)) GROUP BY program, step";
        var used = new Dictionary<string, object>(parameters)
                   {
                       ["$names"] = JsonSerializer.Serialize(ranked.Select(entry => entry.Program))
                   };
        var rows = await ReaderQuery.ReadAsync(connection,
                                               sql,
                                               used,
                                               reader => (Program: reader.GetString(0),
                                                          Index: reader.GetInt64(1),
                                                          Sum: reader.GetDouble(2),
                                                          Seen: reader.GetInt64(3),
                                                          Minimum: reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                                                          Maximum: reader.IsDBNull(5) ? 0 : reader.GetDouble(5)),
                                               Context,
                                               cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var row in rows)
        {
            var position = totals.Grid.PositionOf(row.Index);

            if (position >= 0 && totals.Snapshots[position] > 0 && points.TryGetValue(row.Program, out var line))
            {
                var snapshots = totals.Snapshots[position];

                line[position] = RollupSteps.Point(row.Sum, row.Seen, row.Minimum, row.Maximum, snapshots);
                totals.Named[position] = TimeSeriesQueries.ClampSum(totals.Named[position] + row.Sum);
            }
        }

        return points;
    }

    #endregion // Methods
}