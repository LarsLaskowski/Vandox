namespace Vandox.Storage;

/// <summary>
/// The availability a unit has gathered so far and its latest record, while <see cref="ServiceAvailabilityFolder"/> folds.
/// </summary>
internal sealed class ServiceUnitTally
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceUnitTally"/> class.
    /// </summary>
    /// <param name="unit">The unit name</param>
    /// <param name="id">The ID of the first record</param>
    /// <param name="capturedAt">The capture time of the first record</param>
    /// <param name="activeState">The active state of the first record</param>
    internal ServiceUnitTally(string unit, long id, DateTimeOffset capturedAt, string activeState)
    {
        Unit = unit;
        Id = id;
        CapturedAt = capturedAt;
        ActiveState = activeState;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the unit name.
    /// </summary>
    internal string Unit { get; }

    /// <summary>
    /// Gets or sets the ID of the latest record.
    /// </summary>
    internal long Id { get; set; }

    /// <summary>
    /// Gets or sets the capture time of the latest record.
    /// </summary>
    internal DateTimeOffset CapturedAt { get; set; }

    /// <summary>
    /// Gets or sets the active state of the latest record.
    /// </summary>
    internal string ActiveState { get; set; }

    /// <summary>
    /// Gets or sets the time counted as available.
    /// </summary>
    internal TimeSpan Available { get; set; }

    /// <summary>
    /// Gets or sets the time counted as not available.
    /// </summary>
    internal TimeSpan NotAvailable { get; set; }

    #endregion // Properties
}