using Microsoft.AspNetCore.Http;

using Vandox.Core.Wire;

namespace Vandox.Backend.Ingest;

/// <summary>
/// What the endpoint knows about one request while it works on it: the request, the time it arrived, the token that ends its waiting, reading and decoding,
/// and the header of the batch once it was read.
/// </summary>
internal sealed class IngestRequest
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestRequest"/> class.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="received">The time the request reached the endpoint</param>
    /// <param name="token">Ends the waiting, reading and decoding: the time budget, the abort of the request and the stop signal</param>
    internal IngestRequest(HttpContext context, DateTimeOffset received, CancellationToken token)
    {
        Context = context;
        Received = received;
        Token = token;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the request.
    /// </summary>
    internal HttpContext Context { get; }

    /// <summary>
    /// Gets the time the request reached the endpoint.
    /// </summary>
    internal DateTimeOffset Received { get; }

    /// <summary>
    /// Gets the token that ends the waiting, reading and decoding.
    /// </summary>
    internal CancellationToken Token { get; }

    /// <summary>
    /// Gets or sets the header of the batch; <c>null</c> until it was read.
    /// </summary>
    internal WireHeader? Header { get; set; }

    #endregion // Properties
}