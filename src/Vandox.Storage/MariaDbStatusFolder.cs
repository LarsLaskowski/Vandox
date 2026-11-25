#pragma warning disable RH2003, S2325, S4487 // Skeleton: removed with the implementation
namespace Vandox.Storage;

/// <summary>
/// Folds the MariaDB status records of a range onto the steps of a time axis: a record holds up to <see cref="Hold"/> (record 0228).
/// </summary>
internal sealed class MariaDbStatusFolder
{
    #region Constants

    /// <summary>
    /// The longest time a status record holds.
    /// </summary>
    internal static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    #endregion // Constants

    #region Fields

    private readonly MariaDbStatusQuery _query;

    private readonly UnknownSpans _unknown;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="MariaDbStatusFolder"/> class.
    /// </summary>
    /// <param name="query">The query</param>
    /// <param name="unknown">The unknown time of the range</param>
    internal MariaDbStatusFolder(MariaDbStatusQuery query, UnknownSpans unknown)
    {
        _query = query;
        _unknown = unknown;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a record; the records come in capture order.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="availability">The availability of the record</param>
    /// <param name="memoryUsed">The memory in use, <c>null</c> when absent</param>
    /// <param name="threadsConnected">The connections, <c>null</c> when absent</param>
    internal void Add(DateTimeOffset capturedAt, string availability, double? memoryUsed, double? threadsConnected)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Ends the fold.
    /// </summary>
    /// <param name="oldest">The capture time of the oldest status record, <c>null</c> when none is stored</param>
    /// <returns>The result</returns>
    internal MariaDbStatusResult Finish(DateTimeOffset? oldest)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore RH2003, S2325, S4487