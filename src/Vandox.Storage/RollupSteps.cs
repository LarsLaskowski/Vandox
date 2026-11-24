using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// The parts the per-program and the connection rollup reads share: the steps, the validation of the range, the clamping and the
/// points.
/// </summary>
internal static class RollupSteps
{
    #region Constants

    /// <summary>
    /// The step index of a bucket: its start divided by the step, rounded towards negative infinity, without a subtraction that could overflow.
    /// </summary>
    internal const string StepIndex = "(bucket / $step - (bucket % $step < 0))";

    private const int MaxDayStep = 36500;

    private const string FromParameter = "$from";

    private const string ToParameter = "$to";

    #endregion // Constants

    #region Fields

    private static readonly TimeSpan[] _steps = [
                                                    TimeSpan.FromMinutes(5),
                                                    TimeSpan.FromMinutes(10),
                                                    TimeSpan.FromMinutes(15),
                                                    TimeSpan.FromMinutes(30),
                                                    TimeSpan.FromHours(1),
                                                    TimeSpan.FromHours(2),
                                                    TimeSpan.FromHours(3),
                                                    TimeSpan.FromHours(6),
                                                    TimeSpan.FromHours(12)
                                                ];

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Tells whether a step is one of the steps of the query: 5, 10, 15 and 30 minutes, 1, 2, 3, 6 and 12 hours, or whole days.
    /// </summary>
    /// <param name="width">The step</param>
    /// <returns><c>true</c> for a step of the query</returns>
    internal static bool IsStep(TimeSpan width)
    {
        if (Array.IndexOf(_steps, width) >= 0)
        {
            return true;
        }

        return width.Ticks >= TimeSpan.TicksPerDay && width.Ticks % TimeSpan.TicksPerDay == 0 && width <= TimeSpan.FromDays(MaxDayStep);
    }

    /// <summary>
    /// Validates the step and the range of a rollup read.
    /// </summary>
    /// <param name="rangeStart">The start of the range</param>
    /// <param name="rangeEnd">The end of the range</param>
    /// <param name="width">The step</param>
    /// <exception cref="InvalidQueryException">The step is none of the query's steps, the range is not storable or holds too many steps</exception>
    internal static void Validate(DateTimeOffset rangeStart, DateTimeOffset rangeEnd, TimeSpan width)
    {
        RecordQueries.Require(IsStep(width), "step must be one of the query's steps");
        RecordQueries.Require(StorageTime.InStorableRange(rangeStart) && StorageTime.InStorableRange(rangeEnd), "from and to must be within the storable range");

        if (rangeStart < rangeEnd)
        {
            var steps = SeriesTime.Overlapping(rangeStart.UtcTicks - SeriesTime.EpochTicks, rangeEnd.UtcTicks - SeriesTime.EpochTicks, width.Ticks);

            RecordQueries.Require(steps <= StorageLimits.MaxProgramRollupSteps, $"the range holds more than {StorageLimits.MaxProgramRollupSteps} steps");
        }
    }

    /// <summary>
    /// Returns a sum expression clamped to the finite range, so that no sum of the read is infinite.
    /// </summary>
    /// <param name="expression">The aggregate expression</param>
    /// <returns>The SQL</returns>
    internal static string Clamp(string expression)
    {
        return $"MIN(MAX(COALESCE({expression}, 0), $low), $max)";
    }

    /// <summary>
    /// Reads one nullable integer from a statement that is an aggregate and so returns exactly one row; a missing value arrives as <c>null</c>.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="sql">The statement</param>
    /// <param name="parameters">The parameters; the statement uses those it names</param>
    /// <param name="context">The context of the errors</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the value, or <c>null</c></returns>
    internal static async Task<long?> ReadNullableAsync(SqliteConnection connection, string sql, Dictionary<string, object> parameters, string context, CancellationToken cancellationToken)
    {
        var rows = await ReaderQuery.ReadAsync(connection, sql, parameters, reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0), context, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        return rows[0];
    }

    /// <summary>
    /// Builds the point of an item in a step: the average over the snapshots, with a minimum of 0 when fewer snapshots name the item than the step holds.
    /// </summary>
    /// <param name="sum">The clamped sum of the values</param>
    /// <param name="seen">The number of snapshots that name the item</param>
    /// <param name="minimum">The smallest value</param>
    /// <param name="maximum">The largest value</param>
    /// <param name="snapshots">The number of snapshots of the step</param>
    /// <returns>The point</returns>
    internal static ProgramRollupPoint Point(double sum, long seen, double minimum, double maximum, long snapshots)
    {
        return new ProgramRollupPoint(sum / snapshots, seen < snapshots ? 0 : minimum, maximum);
    }

    /// <summary>
    /// Returns the average of the items that are not named per step: the total less the named items, kept within 0 and the largest double, so that every value is finite.
    /// </summary>
    /// <param name="sums">The clamped sums of all items per step</param>
    /// <param name="named">The clamped sums of the named items per step</param>
    /// <param name="snapshots">The snapshots per step</param>
    /// <returns>The averages, <c>null</c> for a step without snapshots</returns>
    internal static List<double?> Others(double[] sums, double[] named, long[] snapshots)
    {
        var others = new List<double?>(snapshots.Length);

        for (var position = 0; position < snapshots.Length; position++)
        {
            if (snapshots[position] == 0)
            {
                others.Add(null);

                continue;
            }

            var rest = Math.Clamp(sums[position] - named[position], 0, double.MaxValue);

            others.Add(rest / snapshots[position]);
        }

        return others;
    }

    /// <summary>
    /// Reads the sources that have rollups in a range.
    /// </summary>
    /// <param name="connection">The reader connection</param>
    /// <param name="query">The validated query</param>
    /// <param name="sql">The statement, which reads the source and its latest bucket for <c>$from</c>, <c>$to</c> and <c>$limit</c></param>
    /// <param name="context">The context of the errors</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the sources</returns>
    internal static async Task<ProgramSourceList> ReadSourceListAsync(SqliteConnection connection, ProgramSourceQuery query, string sql, string context, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object>
                         {
                             [FromParameter] = StorageTime.ToNanoseconds(query.From),
                             [ToParameter] = StorageTime.ToNanoseconds(query.To),
                             ["$limit"] = (long)StorageLimits.MaxProgramSources + 1
                         };
        var rows = await ReaderQuery.ReadAsync(connection, sql, parameters, reader => (Source: reader.GetString(0), Bucket: reader.GetInt64(1)), context, cancellationToken).ConfigureAwait(false);

        return new ProgramSourceList
               {
                   Sources = [
                                 .. rows.Take(StorageLimits.MaxProgramSources).Select(row => new ProgramSource
                                                                                             {
                                                                                                 Source = row.Source,
                                                                                                 LatestBucket = StorageTime.FromNanoseconds(row.Bucket)
                                                                                             })
                             ],
                   More = rows.Count > StorageLimits.MaxProgramSources
               };
    }

    #endregion // Methods
}