using System.Text;

using Vandox.Backend.Hosting;
using Vandox.Storage;

namespace Vandox.Backend.Reports;

/// <summary>
/// The endpoint that streams the forensics report as a Markdown download.
/// </summary>
internal static class ReportDownloadEndpoint
{
    #region Constants

    /// <summary>
    /// The route pattern of the endpoint.
    /// </summary>
    internal const string Pattern = "/report/markdown";

    private const string LoggerCategory = "report";

    private const string ContentType = "text/markdown; charset=utf-8";

    private const string NoStore = "no-store";

    private const int BufferChars = 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Maps the endpoint; it needs a session.
    /// </summary>
    /// <param name="endpoints">The route builder</param>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Pattern, DownloadAsync);
    }

    /// <summary>
    /// Writes the report in the language of the address into the response.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="reports">The report service</param>
    /// <param name="clock">The clock of the deadline</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <returns>A task that completes when the response is written</returns>
    internal static async Task DownloadAsync(HttpContext context, ReportService reports, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var address = ReportAddress.Parse(context.Request.QueryString.Value ?? string.Empty);
        var text = ReportText.For(address.Language);

        if (address.Error.Length > 0 || text is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;

            return;
        }

        context.Response.Headers.CacheControl = NoStore;
        context.Response.ContentType = ContentType;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{ReportsView.FileName(address.Language)}\"";

        using var deadline = new CancellationTokenSource(ReportsView.Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);

        try
        {
            await WriteAsync(context, reports, text, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            Fail(context, StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is StoreException or InvalidOperationException)
        {
            loggerFactory.CreateLogger(LoggerCategory).ReportDownloadFailed(exception);
            Fail(context, StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Runs the report into the body of the response; every part reaches the client when it is written.
    /// The writer is flushed and disposed only after a complete run, because its disposal flushes the body and a flush starts the response on the server, so a failed run leaves it undisposed (no unmanaged resource, body left open).
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="reports">The report service</param>
    /// <param name="text">The labels of the language</param>
    /// <param name="cancellationToken">Ends the run</param>
    /// <returns>A task that completes when the report is written and flushed</returns>
    private static async Task WriteAsync(HttpContext context, ReportService reports, ReportText text, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(context.Response.Body, new UTF8Encoding(false), BufferChars, true);

        await reports.RunAsync(new MarkdownReportWriter(writer, text), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await writer.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a failed run: a status without a body when the response has not started, otherwise the connection is aborted so that no truncated file is saved.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="statusCode">The status to answer</param>
    private static void Fail(HttpContext context, int statusCode)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();

            return;
        }

        context.Response.Headers.Remove("Content-Disposition");
        context.Response.ContentType = null;
        context.Response.StatusCode = statusCode;
    }

    #endregion // Methods
}