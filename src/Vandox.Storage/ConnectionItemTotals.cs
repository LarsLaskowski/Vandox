namespace Vandox.Storage;

// Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003, S4487
/// <summary>
/// The totals of one state, program or address within a connection bucket.
/// </summary>
internal sealed class ConnectionItemTotals
{
    #region Fields

    private readonly double _value;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionItemTotals"/> class.
    /// </summary>
    /// <param name="value">The value of the first snapshot that names the item</param>
    internal ConnectionItemTotals(double value)
    {
        _value = value;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots that name the item.
    /// </summary>
    internal long Seen => throw new NotImplementedException();

    /// <summary>
    /// Gets the smallest value.
    /// </summary>
    internal double Minimum => throw new NotImplementedException();

    /// <summary>
    /// Gets the largest value.
    /// </summary>
    internal double Maximum => throw new NotImplementedException();

    /// <summary>
    /// Gets the sum of the values, limited to the range of a double.
    /// </summary>
    internal double Sum => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds the value of a further snapshot.
    /// </summary>
    /// <param name="value">The value</param>
    internal void Add(double value)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

// Skeleton: removed with the implementation
#pragma warning restore S2325, RH2003, S4487