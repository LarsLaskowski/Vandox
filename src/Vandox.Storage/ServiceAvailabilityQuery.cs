namespace Vandox.Storage;

/// <summary>
/// A query for the availability of the systemd units of a range.
/// </summary>
public sealed class ServiceAvailabilityQuery
{
    #region Properties

    /// <summary>
    /// Gets the start of the range.
    /// </summary>
    public DateTimeOffset From { get; init; }

    /// <summary>
    /// Gets the end of the range.
    /// </summary>
    public DateTimeOffset To { get; init; }

    #endregion // Properties
}