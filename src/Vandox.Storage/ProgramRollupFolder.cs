using System.Text.Json;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Folds the process snapshots added since the last step into the per-program 5-minute and hourly buckets and the totals of
/// their source.
/// </summary>
internal static class ProgramRollupFolder
{
    #region Constants

    private const long FiveMinutesNs = 300_000_000_000;

    private const long HourNs = 3_600_000_000_000;

    private const string SourceParameter = "$source";

    private const string BucketParameter = "$bucket";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Runs one fold step in the open transaction of the session.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="policy">The retention policy: buckets past the period of their tier are not created</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the step between rows</param>
    /// <returns>The result of the step</returns>
    internal static RollupStepResult Fold(RetentionSession session, RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var after = session.ReadNumber(RetentionQueries.ProgramRollupWatermarkKey);
        ProgramRollupTier[] tiers = [new(FiveMinutesNs, RollupFolder.HorizonOf(policy.FiveMinuteRollups, now), false), new(HourNs, RollupFolder.HorizonOf(policy.HourlyRollups, now), true)];
        var rows = 0;
        var folded = 0;
        var lastId = after;
        var bytes = 0L;
        var budgetSpent = false;

        using (var reader = session.Statement(RetentionQueries.ReadProcessSnapshots).Set("$after", after).Set("$limit", (long)StorageLimits.ProgramRollupStepRows).Query())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var data = reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3);

                lastId = reader.GetInt64(0);
                rows++;
                bytes += data?.Length ?? 0;

                if (Decode(data) is { } snapshot)
                {
                    FoldSnapshot(tiers, reader.GetString(1), reader.GetInt64(2), snapshot);
                    folded++;
                }

                if (bytes > StorageLimits.ProgramRollupStepBytes)
                {
                    budgetSpent = true;

                    break;
                }
            }
        }

        if (rows == 0)
        {
            return new RollupStepResult();
        }

        var written = tiers.Sum(tier => Write(session, tier, cancellationToken));

        session.WriteNumber(RetentionQueries.ProgramRollupWatermarkKey, lastId);

        return new RollupStepResult
               {
                   Snapshots = folded,
                   SkippedSnapshots = rows - folded,
                   ProgramBuckets = written,
                   More = budgetSpent || rows >= StorageLimits.ProgramRollupStepRows
               };
    }

    /// <summary>
    /// Decodes and validates the stored data of a process snapshot.
    /// </summary>
    /// <param name="data">The stored UTF-8 bytes, or <c>null</c> when the column is empty</param>
    /// <returns>The snapshot, or <c>null</c> when the data does not decode into a valid process snapshot</returns>
    private static ProcessSnapshot? Decode(byte[]? data)
    {
        if (data is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);

            return PayloadRegistry.Deserialize(RecordKind.ProcessSnapshot, document.RootElement) is ProcessSnapshot { } snapshot && snapshot.Validate() is null ? snapshot : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a validated snapshot to the buckets of its source in every tier that still keeps them.
    /// </summary>
    /// <param name="tiers">The tiers</param>
    /// <param name="source">The source of the snapshot</param>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <param name="snapshot">The snapshot</param>
    private static void FoldSnapshot(ProgramRollupTier[] tiers, string source, long capturedAt, ProcessSnapshot snapshot)
    {
        var programs = snapshot.Programs ?? [];
        var pssSnapshot = programs.Count > 0 && programs.All(program => program.PssBytes is not null);
        var rss = 0D;
        var pss = 0D;
        var cpu = 0D;

        foreach (var program in programs)
        {
            rss = Clamp(rss + program.RssBytes);
            pss = Clamp(pss + (program.PssBytes ?? 0));
            cpu = Clamp(cpu + program.CpuPercent);
        }

        var incomplete = snapshot.ListsEveryProgram() == false;
        var named = Named(programs, pssSnapshot);

        foreach (var tier in tiers)
        {
            if (tier.BucketOf(source, capturedAt) is not { } bucket)
            {
                continue;
            }

            bucket.Totals.Include(rss, pssSnapshot ? pss : null, cpu, incomplete);

            foreach (var program in named)
            {
                bucket.Add(program.Program, program.RssBytes, pssSnapshot ? program.PssBytes : null, program.CpuPercent);
            }
        }
    }

    /// <summary>
    /// Selects the programs a snapshot names in its buckets: all of them, or above the cut per key the highest in the memory key of the snapshot together with the highest by CPU.
    /// </summary>
    /// <param name="programs">The programs of the snapshot</param>
    /// <param name="pssSnapshot">Whether the memory key is the proportional memory</param>
    /// <returns>The selected programs in ordinal name order, which is the fold order</returns>
    private static List<ProgramAggregate> Named(List<ProgramAggregate> programs, bool pssSnapshot)
    {
        var ordered = programs.OrderBy(program => program.Program, StringComparer.Ordinal).ToList();

        if (ordered.Count <= StorageLimits.ProgramCutPerKey)
        {
            return ordered;
        }

        var byMemory = ordered.OrderByDescending(program => pssSnapshot ? program.PssBytes ?? 0 : program.RssBytes).Take(StorageLimits.ProgramCutPerKey);
        var byCpu = ordered.OrderByDescending(program => program.CpuPercent).Take(StorageLimits.ProgramCutPerKey);

        return [.. byMemory.Union(byCpu).OrderBy(program => program.Program, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Limits a sum to the range of a double.
    /// </summary>
    /// <param name="sum">The sum, which may be infinite</param>
    /// <returns>The sum within the largest finite values</returns>
    private static double Clamp(double sum)
    {
        return Math.Clamp(sum, -double.MaxValue, double.MaxValue);
    }

    /// <summary>
    /// Merges the buckets of one tier into its tables.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>The number of buckets written</returns>
    private static int Write(RetentionSession session, ProgramRollupTier tier, CancellationToken cancellationToken)
    {
        foreach (var (key, bucket) in tier.Buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var named = ReadNamed(session, tier, key);

            foreach (var (program, totals) in bucket.Programs)
            {
                if (RetentionSession.Exists(session.Statement(tier.HasProgram).Set(SourceParameter, key.Source).Set(BucketParameter, key.Bucket).Set("$program", program)) == false)
                {
                    if (named >= StorageLimits.MaxNamedPrograms)
                    {
                        continue;
                    }

                    named++;
                }

                WriteProgram(session, tier, key, program, totals);
            }

            WriteTotal(session, tier, key, bucket.Totals, named);
        }

        return tier.Buckets.Count;
    }

    /// <summary>
    /// Reads the number of programs a stored bucket names.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <returns>The number, or 0 when the bucket is not stored</returns>
    private static long ReadNamed(RetentionSession session, ProgramRollupTier tier, SourceBucketKey key)
    {
        using var reader = session.Statement(tier.ReadNamed).Set(SourceParameter, key.Source).Set(BucketParameter, key.Bucket).Query();

        return reader.Read() ? reader.GetInt64(0) : 0;
    }

    /// <summary>
    /// Merges one program of a bucket into its table.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <param name="program">The program name</param>
    /// <param name="totals">The values to merge</param>
    private static void WriteProgram(RetentionSession session, ProgramRollupTier tier, SourceBucketKey key, string program, ProgramBucketTotals totals)
    {
        session.Statement(tier.UpsertProgram)
               .Set(SourceParameter, key.Source)
               .Set(BucketParameter, key.Bucket)
               .Set("$program", program)
               .Set("$seen", totals.Seen)
               .Set("$rssMin", totals.RssMinimum)
               .Set("$rssMax", totals.RssMaximum)
               .Set("$rssSum", totals.RssSum)
               .Set("$pssMin", totals.PssMinimum)
               .Set("$pssMax", totals.PssMaximum)
               .Set("$pssSum", totals.PssSum)
               .Set("$cpuMin", totals.CpuMinimum)
               .Set("$cpuMax", totals.CpuMaximum)
               .Set("$cpuSum", totals.CpuSum)
               .Set("$low", -double.MaxValue)
               .Set("$high", double.MaxValue)
               .Execute();
    }

    /// <summary>
    /// Merges the total of a bucket into its table.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <param name="totals">The values to merge</param>
    /// <param name="named">The number of programs the bucket names after this step</param>
    private static void WriteTotal(RetentionSession session, ProgramRollupTier tier, SourceBucketKey key, ProgramSourceTotals totals, long named)
    {
        session.Statement(tier.UpsertTotal)
               .Set(SourceParameter, key.Source)
               .Set(BucketParameter, key.Bucket)
               .Set("$snapshots", totals.Snapshots)
               .Set("$pssSnapshots", totals.PssSnapshots)
               .Set("$incomplete", totals.Incomplete)
               .Set("$named", named)
               .Set("$rssSum", totals.RssSum)
               .Set("$pssSum", totals.PssSum)
               .Set("$cpuSum", totals.CpuSum)
               .Set("$low", -double.MaxValue)
               .Set("$high", double.MaxValue)
               .Execute();
    }

    #endregion // Methods
}