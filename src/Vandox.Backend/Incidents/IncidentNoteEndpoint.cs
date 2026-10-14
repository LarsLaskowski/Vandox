#pragma warning disable RH2003, S2325

using Vandox.Storage;

namespace Vandox.Backend.Incidents;

/// <summary>
/// The endpoint that saves a note of an incident.
/// </summary>
internal static class IncidentNoteEndpoint
{
    #region Constants

    /// <summary>
    /// The route pattern of the endpoint.
    /// </summary>
    internal const string Pattern = "/incidents/{id:long}/notes";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Maps the endpoint; it needs a session and a valid antiforgery token.
    /// </summary>
    /// <param name="endpoints">The route builder</param>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Saves the note of the form and redirects to the incident.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="id">The number of the incident</param>
    /// <param name="incidents">The incident browser</param>
    /// <param name="clock">The clock</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <returns>A task that completes when the response is written</returns>
    internal static Task SaveAsync(HttpContext context, long id, IIncidentBrowser incidents, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}