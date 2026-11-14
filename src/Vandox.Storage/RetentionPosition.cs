namespace Vandox.Storage;

/// <summary>
/// A position of the removal walk: the capture time and the ID of the last row looked at.
/// </summary>
internal sealed class RetentionPosition
{
    #region Constants

    private const long Start = -9_223_372_036_854_775_800;

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionPosition"/> class at the start of a stage.
    /// </summary>
    internal RetentionPosition()
    {
        Reset();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionPosition"/> class.
    /// </summary>
    /// <param name="at">The capture time in nanoseconds</param>
    /// <param name="id">The ID</param>
    internal RetentionPosition(long at, long id)
    {
        At = at;
        Id = id;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the capture time in nanoseconds.
    /// </summary>
    internal long At { get; private set; }

    /// <summary>
    /// Gets the ID.
    /// </summary>
    internal long Id { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Moves the position.
    /// </summary>
    /// <param name="at">The capture time in nanoseconds</param>
    /// <param name="id">The ID</param>
    internal void Set(long at, long id)
    {
        At = at;
        Id = id;
    }

    /// <summary>
    /// Moves the position to the start of a stage.
    /// </summary>
    internal void Reset()
    {
        Set(Start, 0);
    }

    #endregion // Methods
}