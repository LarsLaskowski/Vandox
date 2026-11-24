namespace Vandox.Storage;

/// <summary>
/// The totals of one state, program or address within a connection bucket.
/// </summary>
internal sealed class ConnectionItemTotals
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionItemTotals"/> class.
    /// </summary>
    /// <param name="value">The value of the first snapshot that names the item</param>
    internal ConnectionItemTotals(double value)
    {
        Seen = 1;
        Minimum = value;
        Maximum = value;
        Sum = RollupFolder.Clamp(value);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of snapshots that name the item.
    /// </summary>
    internal long Seen { get; private set; }

    /// <summary>
    /// Gets the smallest value.
    /// </summary>
    internal double Minimum { get; private set; }

    /// <summary>
    /// Gets the largest value.
    /// </summary>
    internal double Maximum { get; private set; }

    /// <summary>
    /// Gets the sum of the values, limited to the range of a double.
    /// </summary>
    internal double Sum { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds the value of a further snapshot.
    /// </summary>
    /// <param name="value">The value</param>
    internal void Add(double value)
    {
        Seen++;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
        Sum = RollupFolder.Clamp(Sum + value);
    }

    #endregion // Methods
}