using Microsoft.JSInterop;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IJSRuntime"/> that records every call, hands out <see cref="RecordingJsObjectReference"/> instances and can throw a scripted exception.
/// </summary>
internal sealed class RecordingJsRuntime : IJSRuntime
{
    #region Fields

    private readonly Dictionary<(string Target, string Identifier), Exception> _failures = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the gate that holds back the completion of every <c>create</c> call; <see langword="null"/> completes the call at once.
    /// </summary>
    internal TaskCompletionSource? CreateGate { get; set; }

    /// <summary>
    /// Gets every call of the runtime and of the references it handed out, in the order they were made.
    /// </summary>
    internal List<RecordedJsCall> Calls { get; } = [];

    /// <summary>
    /// Gets the references the runtime handed out, in the order they were created.
    /// </summary>
    internal List<RecordingJsObjectReference> References { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Makes the next calls of an identifier on a target throw an exception.
    /// </summary>
    /// <param name="target">The target: <c>runtime</c>, <c>module</c> or <c>chart</c></param>
    /// <param name="identifier">The identifier; <c>DisposeAsync</c> for a disposal</param>
    /// <param name="exception">The exception to throw</param>
    internal void Fail(string target, string identifier, Exception exception)
    {
        _failures[(target, identifier)] = exception;
    }

    /// <summary>
    /// Records a call of a reference and throws the scripted exception, if any.
    /// </summary>
    /// <param name="call">The call</param>
    internal void Record(RecordedJsCall call)
    {
        Calls.Add(call);

        if (_failures.TryGetValue((call.Target, call.Identifier), out var exception))
        {
            throw exception;
        }
    }

    /// <summary>
    /// Creates a reference for the call that returns it.
    /// </summary>
    /// <param name="identifier">The identifier of the call that returns the reference</param>
    /// <returns>The new reference</returns>
    internal RecordingJsObjectReference CreateReference(string identifier)
    {
        var name = identifier switch
                   {
                       "import" => "module",
                       "create" => "chart",
                       _ => identifier
                   };
        var reference = new RecordingJsObjectReference(this, name);

        References.Add(reference);

        return reference;
    }

    #endregion // Methods

    #region IJSRuntime

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        return InvokeAsync<TValue>(identifier, default, args);
    }

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        Record(new RecordedJsCall("runtime", identifier, args ?? [], cancellationToken));

        if (typeof(TValue) == typeof(IJSObjectReference))
        {
            return ValueTask.FromResult((TValue)(object)CreateReference(identifier));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    #endregion // IJSRuntime
}