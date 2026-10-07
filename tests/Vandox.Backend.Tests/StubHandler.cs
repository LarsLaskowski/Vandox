namespace Vandox.Backend.Tests;

/// <summary>
/// An HTTP handler double that answers with a function and records the URLs it was asked for.
/// </summary>
internal sealed class StubHandler : HttpMessageHandler
{
    #region Fields

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="StubHandler"/> class.
    /// </summary>
    /// <param name="answer">Answers a request, or throws to fail it</param>
    internal StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _answer = answer;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the URLs that were asked for.
    /// </summary>
    internal List<string> Requests { get; } = [];

    #endregion // Properties

    #region HttpMessageHandler

    /// <inheritdoc />
    /// <returns>A task that returns the result</returns>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.ToString());

        return Task.FromResult(_answer(request));
    }

    #endregion // HttpMessageHandler
}