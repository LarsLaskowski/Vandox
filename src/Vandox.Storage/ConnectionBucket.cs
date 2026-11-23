namespace Vandox.Storage;

// Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
/// <summary>
/// One connection bucket a fold step adds to: the totals of its source and the states, programs and addresses it names.
/// </summary>
internal sealed class ConnectionBucket
{
    #region Properties

    /// <summary>
    /// Gets the number of snapshots added to the bucket.
    /// </summary>
    internal long Snapshots => throw new NotImplementedException();

    /// <summary>
    /// Gets the number of added snapshots with incomplete aggregates.
    /// </summary>
    internal long Incomplete => throw new NotImplementedException();

    /// <summary>
    /// Gets the sum of the connections of all processes of the added snapshots.
    /// </summary>
    internal double ProcessSum => throw new NotImplementedException();

    /// <summary>
    /// Gets the states, programs and addresses of the bucket in the order they were first met.
    /// </summary>
    internal IReadOnlyList<(int Kind, string Name, ConnectionItemTotals Totals)> Items => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a snapshot to the totals of the bucket.
    /// </summary>
    /// <param name="processSum">The sum of the connections of all processes of the snapshot</param>
    /// <param name="incomplete">Whether the aggregates of the snapshot are incomplete</param>
    internal void Include(double processSum, bool incomplete)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds the value of a state or a program of a snapshot.
    /// </summary>
    /// <param name="kind">The kind: 0 a state, 1 a program</param>
    /// <param name="name">The name</param>
    /// <param name="value">The value</param>
    internal void Add(int kind, string name, double value)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds the value of a remote address of a snapshot.
    /// </summary>
    /// <param name="address">The address</param>
    /// <param name="value">The value</param>
    /// <param name="snapshot">The ordinal of the snapshot within the step</param>
    internal void AddAddress(string address, double value, int snapshot)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Gets the ordinals of the snapshots that named an address.
    /// </summary>
    /// <param name="address">The address</param>
    /// <returns>The ordinals, empty when no snapshot named the address</returns>
    internal IReadOnlyList<int> SnapshotsOf(string address)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

// Skeleton: removed with the implementation
#pragma warning restore S2325, RH2003