using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Vandox.Backend.Tests;

/// <summary>
/// One ingest request as the endpoint gets it: an <see cref="HttpContext"/> with the headers of a correct post, which a test changes before it runs the call.
/// </summary>
internal sealed class IngestCall : IDisposable
{
    #region Constants

    /// <summary>
    /// The address of the sender as the service sees it.
    /// </summary>
    internal const string Peer = "203.0.113.9";

    #endregion // Constants

    #region Fields

    private readonly CancellationTokenSource _abort = new();

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestCall"/> class.
    /// </summary>
    /// <param name="body">The body the call serves</param>
    /// <param name="token">The token of the <c>Authorization</c> header; the header is left out when <c>null</c></param>
    /// <param name="contentLength">The length the call announces, <c>null</c> for none</param>
    internal IngestCall(CountingBodyStream body, string? token, long? contentLength)
    {
        Body = body;
        Context = new DefaultHttpContext();
        Context.Request.Method = HttpMethods.Post;
        Context.Request.Path = "/v1/batches";
        Context.Request.ContentType = "application/gzip";
        Context.Request.Body = body;
        Context.Response.Body = new MemoryStream();
        Context.Connection.RemoteIpAddress = IPAddress.Parse(Peer);
        Context.RequestAborted = _abort.Token;
        Context.Features.Set<IHttpMaxRequestBodySizeFeature>(new FakeMaxRequestBodySizeFeature());

        if (token is not null)
        {
            Context.Request.Headers.Authorization = $"Bearer {token}";
        }

        Context.Request.ContentLength = contentLength;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the context the endpoint handles.
    /// </summary>
    internal DefaultHttpContext Context { get; }

    /// <summary>
    /// Gets the body that counts its reads.
    /// </summary>
    internal CountingBodyStream Body { get; }

    /// <summary>
    /// Gets the status code of the answer.
    /// </summary>
    internal int Status => Context.Response.StatusCode;

    /// <summary>
    /// Gets the value of the <c>Vandox-Refused</c> header, <c>null</c> when the answer carries none.
    /// </summary>
    internal string? Refused => Header("Vandox-Refused");

    /// <summary>
    /// Gets the number of bytes the answer wrote to its body.
    /// </summary>
    internal long AnswerBytes => ((MemoryStream)Context.Response.Body).Length;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates a call that posts the bytes with the token and announces their length.
    /// </summary>
    /// <param name="body">The compressed batch</param>
    /// <param name="token">The token, <c>null</c> for none</param>
    /// <returns>The call</returns>
    internal static IngestCall Post(byte[] body, string? token = IngestRig.Token)
    {
        return new IngestCall(new CountingBodyStream(body), token, body.Length);
    }

    /// <summary>
    /// Creates a call whose body does not announce its length, as a chunked body does not.
    /// </summary>
    /// <param name="body">The body</param>
    /// <param name="token">The token, <c>null</c> for none</param>
    /// <returns>The call</returns>
    internal static IngestCall Chunked(CountingBodyStream body, string? token = IngestRig.Token)
    {
        return new IngestCall(body, token, null);
    }

    /// <summary>
    /// Sets a request header to the given values, or removes it when there are none.
    /// </summary>
    /// <param name="name">The name of the header</param>
    /// <param name="values">The values</param>
    /// <returns>This call</returns>
    internal IngestCall With(string name, params string[] values)
    {
        if (values.Length == 0)
        {
            Context.Request.Headers.Remove(name);
        }
        else
        {
            Context.Request.Headers[name] = new StringValues(values);
        }

        return this;
    }

    /// <summary>
    /// Sets the announced length of the body.
    /// </summary>
    /// <param name="length">The length, <c>null</c> for none</param>
    /// <returns>This call</returns>
    internal IngestCall WithLength(long? length)
    {
        Context.Request.ContentLength = length;

        return this;
    }

    /// <summary>
    /// Aborts the request as a closed connection does.
    /// </summary>
    /// <returns>A task that completes when the abort ran</returns>
    internal Task AbortAsync()
    {
        return _abort.CancelAsync();
    }

    /// <summary>
    /// Gets the values of a response header.
    /// </summary>
    /// <param name="name">The name of the header</param>
    /// <returns>The value, <c>null</c> when the answer carries none</returns>
    internal string? Header(string name)
    {
        return Context.Response.Headers.TryGetValue(name, out var values) ? values.ToString() : null;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _abort.Dispose();
        Context.Response.Body.Dispose();
    }

    #endregion // IDisposable
}