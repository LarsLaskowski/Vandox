namespace Vandox.Storage;

/// <summary>
/// A time window whose data retention keeps.
/// </summary>
public sealed class RetentionHold
{
    #region Properties

    /// <summary>
    /// Gets the start of the window.
    /// </summary>
    public DateTimeOffset Start { get; init; }

    /// <summary>
    /// Gets the end of the window.
    /// </summary>
    public DateTimeOffset End { get; init; }

    #endregion // Properties
}