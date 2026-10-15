namespace Vandox.Backend.Reports;

#pragma warning disable RH2003, S2325

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

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Maps the endpoint; it needs a session.
    /// </summary>
    /// <param name="endpoints">The route builder</param>
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the report in the language of the address into the response.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="reports">The report service</param>
    /// <param name="clock">The clock of the deadline</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <returns>A task that completes when the response is written</returns>
    internal static Task DownloadAsync(HttpContext context, ReportService reports, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}