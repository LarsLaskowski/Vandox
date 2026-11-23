using System.Globalization;

using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// Folds the connection snapshots added since the last step into the per-state, per-program and per-address 5-minute and hourly
/// buckets of their source, and trims the addresses of ended buckets.
/// </summary>
internal static class ConnectionRollupFolder
{
    #region Constants

    /// <summary>
    /// The kind of a row that holds a state.
    /// </summary>
    internal const int StateKind = 0;

    /// <summary>
    /// The kind of a row that holds a program.
    /// </summary>
    internal const int ProgramKind = 1;

    /// <summary>
    /// The kind of a row that holds a remote address.
    /// </summary>
    internal const int AddressKind = 2;

    private const long FiveMinutesNs = 300_000_000_000;

    private const long HourNs = 3_600_000_000_000;

    private const string UdpState = "UDP";

    private const string SourceParameter = "$source";

    private const string BucketParameter = "$bucket";

    private const string KindParameter = "$kind";

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
        var after = session.ReadNumber(ConnectionRollupSql.WatermarkKey);
        ConnectionRollupTier[] tiers = [new(FiveMinutesNs, RollupFolder.HorizonOf(policy.FiveMinuteRollups, now), false), new(HourNs, RollupFolder.HorizonOf(policy.HourlyRollups, now), true)];
        var rows = 0;
        var folded = 0;
        var lastId = after;
        var bytes = 0L;
        var budgetSpent = false;

        using (var reader = session.Statement(ConnectionRollupSql.ReadSnapshots).Set("$after", after).Set("$limit", (long)StorageLimits.ConnectionRollupStepRows).Query())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var data = reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3);

                lastId = reader.GetInt64(0);
                rows++;
                bytes += data?.Length ?? 0;

                if (RollupFolder.Decode<ConnectionSnapshot>(data, RecordKind.ConnectionSnapshot) is { } snapshot)
                {
                    FoldSnapshot(tiers, reader.GetString(1), reader.GetInt64(2), snapshot, folded);
                    folded++;
                }

                if (bytes > StorageLimits.ConnectionRollupStepBytes)
                {
                    budgetSpent = true;

                    break;
                }
            }
        }

        var written = 0;

        if (rows > 0)
        {
            written = tiers.Sum(tier => Write(session, tier, cancellationToken));

            session.WriteNumber(ConnectionRollupSql.WatermarkKey, lastId);
        }

        var caughtUp = budgetSpent == false && rows < StorageLimits.ConnectionRollupStepRows;
        var trimmed = 0;
        var moreToTrim = false;

        if (caughtUp)
        {
            foreach (var tier in tiers)
            {
                var (removed, full) = Trim(session, tier, now, cancellationToken);

                trimmed += removed;
                moreToTrim |= full;
            }
        }

        return new RollupStepResult
               {
                   ConnectionSnapshots = folded,
                   SkippedConnectionSnapshots = rows - folded,
                   ConnectionBuckets = written,
                   TrimmedAddresses = trimmed,
                   More = caughtUp == false || moreToTrim
               };
    }

    /// <summary>
    /// Adds a validated snapshot to the buckets of its source in every tier that still keeps them.
    /// </summary>
    /// <param name="tiers">The tiers</param>
    /// <param name="source">The source of the snapshot</param>
    /// <param name="capturedAt">The capture time of the snapshot</param>
    /// <param name="snapshot">The snapshot</param>
    /// <param name="ordinal">The ordinal of the snapshot within the step</param>
    private static void FoldSnapshot(ConnectionRollupTier[] tiers, string source, long capturedAt, ConnectionSnapshot snapshot, int ordinal)
    {
        var states = SumStates(snapshot.States ?? []);
        var (programs, processSum) = SumPrograms(snapshot.Processes ?? []);

        var addresses = SumAddresses(snapshot.Remotes ?? []);
        var incomplete = snapshot.AggregatesIncomplete;

        foreach (var tier in tiers)
        {
            if (tier.BucketOf(source, capturedAt) is not { } bucket)
            {
                continue;
            }

            bucket.Include(processSum, incomplete);

            foreach (var (name, value) in states)
            {
                bucket.Add(StateKind, name, value);
            }

            foreach (var (name, value) in programs)
            {
                bucket.Add(ProgramKind, name, value);
            }

            foreach (var (name, value) in addresses)
            {
                bucket.AddAddress(name, value, ordinal);
            }
        }
    }

    /// <summary>
    /// Adds the counts of a snapshot per state, the TCP protocols under their state and the UDP protocols under <c>UDP</c>.
    /// </summary>
    /// <param name="entries">The state entries</param>
    /// <returns>The states in ordinal name order</returns>
    private static List<(string Name, double Value)> SumStates(List<StateCount> entries)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var name = entry.Proto is Proto.Udp or Proto.Udp6 ? UdpState : entry.State;

            sums[name] = RollupFolder.Clamp(sums.GetValueOrDefault(name) + entry.Count);
        }

        return [.. sums.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value))];
    }

    /// <summary>
    /// Adds the counts of a snapshot per program and selects the programs it names: all of them, or above the cut the highest sums.
    /// </summary>
    /// <param name="processes">The processes</param>
    /// <returns>The selected programs in ordinal name order, which is the fold order, and the sum over all processes</returns>
    private static (List<(string Name, double Value)> Programs, double ProcessSum) SumPrograms(List<ProcessConnections> processes)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        var processSum = 0D;

        foreach (var process in processes)
        {
            sums[process.Command] = RollupFolder.Clamp(sums.GetValueOrDefault(process.Command) + process.Count);
            processSum = RollupFolder.Clamp(processSum + process.Count);
        }

        IEnumerable<KeyValuePair<string, double>> selected = sums;

        if (sums.Count > StorageLimits.ConnectionProgramCut)
        {
            selected = sums.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(StorageLimits.ConnectionProgramCut);
        }

        return ([.. selected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value))], processSum);
    }

    /// <summary>
    /// Adds the counts of a snapshot per remote address and selects the addresses it names: the highest counts up to the cut.
    /// </summary>
    /// <param name="remotes">The remote addresses</param>
    /// <returns>The selected addresses by count descending, then address text in ordinal order, which is the fold order</returns>
    private static List<(string Name, double Value)> SumAddresses(List<RemoteCount> remotes)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var remote in remotes)
        {
            var address = string.Create(CultureInfo.InvariantCulture, $"{remote.Addr}");

            sums[address] = RollupFolder.Clamp(sums.GetValueOrDefault(address) + remote.Count);
        }

        return [.. sums.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(StorageLimits.ConnectionAddressCut).Select(pair => (pair.Key, pair.Value))];
    }

    /// <summary>
    /// Merges the buckets of one tier into its tables.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="cancellationToken">Cancels the write</param>
    /// <returns>The number of buckets written</returns>
    private static int Write(RetentionSession session, ConnectionRollupTier tier, CancellationToken cancellationToken)
    {
        foreach (var (key, bucket) in tier.Buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (stored, programs, addresses) = ReadCounts(session, tier, key);

            var capped = new HashSet<int>();

            foreach (var (kind, name, totals) in bucket.Items)
            {
                if (kind != StateKind && (stored == false || Exists(session, tier, key, kind, name) == false))
                {
                    if (kind == ProgramKind)
                    {
                        if (programs >= StorageLimits.MaxConnectionPrograms)
                        {
                            continue;
                        }

                        programs++;
                    }
                    else
                    {
                        if (addresses >= tier.AddressCap)
                        {
                            capped.UnionWith(bucket.SnapshotsOf(name));

                            continue;
                        }

                        addresses++;
                    }
                }

                WriteItem(session, tier, key, kind, name, totals);
            }

            WriteTotal(session, tier, key, bucket, capped.Count, programs, addresses);
        }

        return tier.Buckets.Count;
    }

    /// <summary>
    /// Reads the number of programs and addresses a stored bucket names.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <returns>Whether the bucket is stored, and its programs and addresses, 0 when it is not</returns>
    private static (bool Stored, long Programs, long Addresses) ReadCounts(RetentionSession session, ConnectionRollupTier tier, SourceBucketKey key)
    {
        using var reader = session.Statement(tier.ReadCounts).Set(SourceParameter, key.Source).Set(BucketParameter, key.Bucket).Query();

        return reader.Read() ? (true, reader.GetInt64(0), reader.GetInt64(1)) : (false, 0, 0);
    }

    /// <summary>
    /// Tells whether a program or an address has a row in a stored bucket.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <param name="kind">The kind of the row</param>
    /// <param name="name">The name</param>
    /// <returns><c>true</c> when the row exists</returns>
    private static bool Exists(RetentionSession session, ConnectionRollupTier tier, SourceBucketKey key, int kind, string name)
    {
        return RetentionSession.Exists(session.Statement(tier.HasItem).Set(SourceParameter, key.Source).Set(BucketParameter, key.Bucket).Set(KindParameter, (long)kind).Set("$name", name));
    }

    /// <summary>
    /// Merges one state, program or address of a bucket into its table.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="key">The bucket</param>
    /// <param name="kind">The kind of the row</param>
    /// <param name="name">The name</param>
    /// <param name="totals">The values to merge</param>
    private static void WriteItem(RetentionSession session, ConnectionRollupTier tier, SourceBucketKey key, int kind, string name, ConnectionItemTotals totals)
    {
        session.Statement(tier.UpsertItem)
               .Set(SourceParameter, key.Source)
               .Set(BucketParameter, key.Bucket)
               .Set(KindParameter, (long)kind)
               .Set("$name", name)
               .Set("$seen", totals.Seen)
               .Set("$min", totals.Minimum)
               .Set("$max", totals.Maximum)
               .Set("$sum", totals.Sum)
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
    /// <param name="bucket">The values to merge</param>
    /// <param name="capped">The number of snapshots that lost an address to the cap in this step</param>
    /// <param name="programs">The number of programs the bucket names after this step</param>
    /// <param name="addresses">The number of addresses the bucket names after this step</param>
    private static void WriteTotal(RetentionSession session, ConnectionRollupTier tier, SourceBucketKey key, ConnectionBucket bucket, int capped, long programs, long addresses)
    {
        session.Statement(tier.UpsertTotal)
               .Set(SourceParameter, key.Source)
               .Set(BucketParameter, key.Bucket)
               .Set("$snapshots", bucket.Snapshots)
               .Set("$incomplete", bucket.Incomplete)
               .Set("$capped", (long)capped)
               .Set("$programs", programs)
               .Set("$addresses", addresses)
               .Set("$processSum", bucket.ProcessSum)
               .Set("$low", -double.MaxValue)
               .Set("$high", double.MaxValue)
               .Execute();
    }

    /// <summary>
    /// Trims the oldest ended buckets of a tier that hold more addresses than the tier keeps to their addresses with the highest sum.
    /// </summary>
    /// <param name="session">The session of the step</param>
    /// <param name="tier">The tier</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the trim between buckets</param>
    /// <returns>The number of address rows removed, and whether the step trimmed as many buckets as it may</returns>
    private static (int Removed, bool Full) Trim(RetentionSession session, ConnectionRollupTier tier, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var upper = StorageTime.ToNanoseconds(now) - StorageLimits.ConnectionTrimDelayNanoseconds - tier.Width;
        var buckets = new List<(SourceBucketKey Key, long Addresses)>();

        using (var reader = session.Statement(tier.ReadTrim).Set("$upper", upper).Set("$limit", (long)StorageLimits.ConnectionTrimBucketsPerStep).Query())
        {
            while (reader.Read())
            {
                buckets.Add((new SourceBucketKey(reader.GetString(0), reader.GetInt64(1)), reader.GetInt64(2)));
            }
        }

        var removed = 0;

        foreach (var (key, addresses) in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deleted = session.Statement(tier.DeleteTrimmed).Set(SourceParameter, key.Source).Set(BucketParameter, key.Bucket).Set("$kept", (long)tier.KeptAddresses).Execute();

            session.Statement(tier.SetTrimmed)
                   .Set(SourceParameter, key.Source)
                   .Set(BucketParameter, key.Bucket)
                   .Set("$addresses", Math.Min(addresses - deleted, tier.KeptAddresses))
                   .Set("$trimmed", (long)deleted)
                   .Execute();

            removed += deleted;
        }

        return (removed, buckets.Count >= StorageLimits.ConnectionTrimBucketsPerStep);
    }

    #endregion // Methods
}