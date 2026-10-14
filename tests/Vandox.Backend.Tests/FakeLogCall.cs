namespace Vandox.Backend.Tests;

/// <summary>
/// One call that <see cref="FakeLogExplorer"/> received.
/// </summary>
internal sealed class FakeLogCall
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeLogCall"/> class.
    /// </summary>
    /// <param name="method">The name of the method</param>
    /// <param name="argument">A copy of the query, or <c>null</c></param>
    /// <param name="cancellationToken">The token of the call</param>
    internal FakeLogCall(string method, object? argument, CancellationToken cancellationToken)
    {
        Method = method;
        Argument = argument;
        CancellationToken = cancellationToken;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the name of the method.
    /// </summary>
    internal string Method { get; }

    /// <summary>
    /// Gets a copy of the query, or <c>null</c>.
    /// </summary>
    internal object? Argument { get; }

    /// <summary>
    /// Gets the token of the call.
    /// </summary>
    internal CancellationToken CancellationToken { get; }

    #endregion // Properties
}