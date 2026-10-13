namespace Vandox.Backend.Tests;

/// <summary>
/// One call that <see cref="RecordingJsRuntime"/> or <see cref="RecordingJsObjectReference"/> recorded.
/// </summary>
internal sealed class RecordedJsCall
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordedJsCall"/> class.
    /// </summary>
    /// <param name="target">The name of the object the call went to: <c>runtime</c>, <c>module</c>, <c>chart</c> or the identifier that created the reference</param>
    /// <param name="identifier">The identifier of the call; <c>DisposeAsync</c> for a disposal</param>
    /// <param name="arguments">The arguments of the call</param>
    /// <param name="cancellationToken">The token of the call</param>
    internal RecordedJsCall(string target, string identifier, object?[] arguments, CancellationToken cancellationToken)
    {
        Target = target;
        Identifier = identifier;
        Arguments = arguments;
        CancellationToken = cancellationToken;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the name of the object the call went to.
    /// </summary>
    internal string Target { get; }

    /// <summary>
    /// Gets the identifier of the call.
    /// </summary>
    internal string Identifier { get; }

    /// <summary>
    /// Gets the arguments of the call.
    /// </summary>
    internal object?[] Arguments { get; }

    /// <summary>
    /// Gets the token of the call; <c>default</c> when the overload without a token was used.
    /// </summary>
    internal CancellationToken CancellationToken { get; }

    #endregion // Properties
}