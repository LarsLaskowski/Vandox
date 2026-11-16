namespace Vandox.Storage;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487
/// <summary>
/// Keeps the smallest label sets of a scan and counts every distinct one.
/// </summary>
internal sealed class LabelSetTally
{
    #region Fields

    private readonly int _limit;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="LabelSetTally"/> class.
    /// </summary>
    /// <param name="limit">The maximum number of label texts kept</param>
    internal LabelSetTally(int limit)
    {
        _limit = limit;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of distinct label texts seen.
    /// </summary>
    internal int Count => throw new NotImplementedException();

    /// <summary>
    /// Gets the kept label texts in ordinal order, each with its first row.
    /// </summary>
    internal IReadOnlyList<(string Labels, DateTimeOffset CapturedAt, double Value)> Kept => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a row, newest first.
    /// </summary>
    /// <param name="labels">The label text</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="value">The value</param>
    internal void Add(string labels, DateTimeOffset capturedAt, double value)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325, S4487