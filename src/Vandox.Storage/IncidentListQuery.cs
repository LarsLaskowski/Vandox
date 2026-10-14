namespace Vandox.Storage;

/// <summary>
/// Selects a page of the incident list, newest first.
/// </summary>
public sealed class IncidentListQuery
{
    #region Properties

    /// <summary>
    /// Gets or sets the position the page lies before; <c>null</c> for the newest page.
    /// </summary>
    public IncidentPosition? Before { get; set; }

    /// <summary>
    /// Gets or sets the most incidents returned.
    /// </summary>
    public int Limit { get; set; }

    #endregion // Properties
}