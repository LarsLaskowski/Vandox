using System.Globalization;

using Microsoft.AspNetCore.Antiforgery;

using Vandox.Backend.Hosting;
using Vandox.Backend.Security;
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

    private const string NoteField = "note";

    private const string WrittenForField = "written_for";

    private const string LoggerCategory = "incidents";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Maps the endpoint; it needs a session and a valid antiforgery token.
    /// </summary>
    /// <param name="endpoints">The route builder</param>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(Pattern, SaveAsync).WithMetadata(RequiredAntiforgery.Instance);
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
    internal static async Task SaveAsync(HttpContext context, long id, IIncidentBrowser incidents, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(incidents);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var feature = context.Features.Get<IAntiforgeryValidationFeature>();

        if (feature is not { IsValid: true })
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        if (id <= 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        var edit = await ReadEditAsync(context, id, clock).ConfigureAwait(false);

        if (edit is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        try
        {
            var outcome = await incidents.SaveIncidentNoteAsync(edit, context.RequestAborted).ConfigureAwait(false);

            Answer(context, id, outcome);
        }
        catch (InvalidNoteException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
        catch (StoreException exception)
        {
            loggerFactory.CreateLogger(LoggerCategory).IncidentNoteSaveFailed(exception);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    /// <summary>
    /// Reads the two fields of the form, and only the form, and checks them.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="id">The number of the incident</param>
    /// <param name="clock">The clock</param>
    /// <returns>A task that returns the edit, or <c>null</c> when the form is not valid</returns>
    private static async Task<IncidentNoteEdit?> ReadEditAsync(HttpContext context, long id, TimeProvider clock)
    {
        var form = await ReadFormAsync(context).ConfigureAwait(false);

        if (form is null || Single(form, NoteField) is not { } note || Single(form, WrittenForField) is not { } writtenForText)
        {
            return null;
        }

        var text = IncidentNoteText.Normalize(note);

        if (IncidentNoteText.IsAcceptable(text) && IncidentNoteText.TryParseWrittenFor(writtenForText, out var writtenFor))
        {
            return new IncidentNoteEdit
                   {
                       IncidentId = id,
                       WrittenFor = writtenFor,
                       Text = text,
                       SavedAt = clock.GetUtcNow()
                   };
        }

        return null;
    }

    /// <summary>
    /// Reads the form of the request, which the middleware has read before and keeps.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that returns the form, or <c>null</c> when the body is no form or cannot be read as one</returns>
    private static async Task<IFormCollection?> ReadFormAsync(HttpContext context)
    {
        if (context.Request.HasFormContentType)
        {
            try
            {
                return await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the value of a field that the form holds exactly once.
    /// </summary>
    /// <param name="form">The form</param>
    /// <param name="name">The name of the field</param>
    /// <returns>The value, or <c>null</c> when the field is missing or given more than once</returns>
    private static string? Single(IFormCollection form, string name)
    {
        return form.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
    }

    /// <summary>
    /// Writes the response of an outcome.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="id">The number of the incident</param>
    /// <param name="outcome">The outcome of the save</param>
    private static void Answer(HttpContext context, long id, IncidentNoteOutcome outcome)
    {
        if (outcome == IncidentNoteOutcome.NotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = string.Create(CultureInfo.InvariantCulture, $"/incidents/{id}");
    }

    #endregion // Methods
}