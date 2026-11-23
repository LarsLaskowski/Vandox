namespace Vandox.Storage;

/// <summary>
/// One connection bucket a fold step adds to: the totals of its source and the states, programs and addresses it names.
/// </summary>
internal sealed class ConnectionBucket
{
    #region Fields

    private readonly Dictionary<(int Kind, string Name), ConnectionItemTotals> _totals = [];
    private readonly List<(int Kind, string Name, ConnectionItemTotals Totals)> _items = [];
    private readonly Dictionary<string, List<int>> _snapshots = [with(StringComparer.Ordinal)];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of snapshots added to the bucket.
    /// </summary>
    internal long Snapshots { get; private set; }

    /// <summary>
    /// Gets the number of added snapshots with incomplete aggregates.
    /// </summary>
    internal long Incomplete { get; private set; }

    /// <summary>
    /// Gets the sum of the connections of all processes of the added snapshots.
    /// </summary>
    internal double ProcessSum { get; private set; }

    /// <summary>
    /// Gets the states, programs and addresses of the bucket in the order they were first met.
    /// </summary>
    internal IReadOnlyList<(int Kind, string Name, ConnectionItemTotals Totals)> Items => _items;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot to the totals of the bucket.
    /// </summary>
    /// <param name="processSum">The sum of the connections of all processes of the snapshot</param>
    /// <param name="incomplete">Whether the aggregates of the snapshot are incomplete</param>
    internal void Include(double processSum, bool incomplete)
    {
        Snapshots++;
        ProcessSum = RollupFolder.Clamp(ProcessSum + processSum);

        if (incomplete)
        {
            Incomplete++;
        }
    }

    /// <summary>
    /// Adds the value of a state or a program of a snapshot.
    /// </summary>
    /// <param name="kind">The kind: 0 a state, 1 a program</param>
    /// <param name="name">The name</param>
    /// <param name="value">The value</param>
    internal void Add(int kind, string name, double value)
    {
        if (_totals.TryGetValue((kind, name), out var totals))
        {
            totals.Add(value);

            return;
        }

        totals = new ConnectionItemTotals(value);
        _totals[(kind, name)] = totals;
        _items.Add((kind, name, totals));
    }

    /// <summary>
    /// Adds the value of a remote address of a snapshot.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="value">The value</param>
    /// <param name="snapshot">The ordinal of the snapshot within the step</param>
    internal void AddAddress(string address, double value, int snapshot)
    {
        Add(ConnectionRollupFolder.AddressKind, address, value);

        if (_snapshots.TryGetValue(address, out var ordinals) == false)
        {
            ordinals = [];
            _snapshots[address] = ordinals;
        }

        ordinals.Add(snapshot);
    }

    /// <summary>
    /// Gets the ordinals of the snapshots that named an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The ordinals</returns>
    internal IReadOnlyList<int> SnapshotsOf(string address)
    {
        return _snapshots[address];
    }

    #endregion // Methods
}