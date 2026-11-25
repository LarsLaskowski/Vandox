#pragma warning disable RH2003, S2325, S4487 // Skeleton: removed with the implementation
namespace Vandox.Storage;

/// <summary>
/// Folds the service state records of a range into the availability of each unit: a state holds up to <see cref="Hold"/>, and at most <see cref="MaxUnits"/> units are kept (record 0227).
/// </summary>
internal sealed class ServiceAvailabilityFolder
{
    #region Constants

    /// <summary>
    /// The most units kept.
    /// </summary>
    internal const int MaxUnits = 256;

    /// <summary>
    /// The longest time a recorded state holds.
    /// </summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromMinutes(65);

    #endregion // Constants

    #region Fields

    private readonly DateTimeOffset _from;

    private readonly DateTimeOffset _to;

    private readonly UnknownSpans _unknown;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceAvailabilityFolder"/> class.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <param name="unknown">The unknown time of the range</param>
    internal ServiceAvailabilityFolder(DateTimeOffset from, DateTimeOffset to, UnknownSpans unknown)
    {
        _from = from;
        _to = to;
        _unknown = unknown;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether a unit name was skipped or evicted.
    /// </summary>
    internal bool Dropped => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a record; the records come in capture order.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="unit">The unit name</param>
    /// <param name="activeState">The active state</param>
    internal void Add(long id, DateTimeOffset capturedAt, string unit, string activeState)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Ends the fold.
    /// </summary>
    /// <returns>The units sorted by name, each with the ID of its latest record</returns>
    internal IReadOnlyList<(ServiceAvailability Row, long LastId)> Finish()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325, S4487