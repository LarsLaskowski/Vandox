namespace Vandox.Storage;

/// <summary>
/// What saving an incident did.
/// </summary>
public enum IncidentSaveOutcome
{
    /// <summary>
    /// A new incident was stored.
    /// </summary>
    Created,

    /// <summary>
    /// A stored incident was changed.
    /// </summary>
    Updated,

    /// <summary>
    /// The stored incident was equal.
    /// </summary>
    Unchanged
}