using Microsoft.JSInterop;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IJSObjectReference"/> that records its calls and its disposal in the call list of its <see cref="RecordingJsRuntime"/>.
/// </summary>
internal sealed class RecordingJsObjectReference : IJSObjectReference
{
    #region Fields

    private readonly RecordingJsRuntime _runtime;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingJsObjectReference"/> class.
    /// </summary>
    /// <param name="runtime">The runtime that hands out the reference</param>
    /// <param name="name">The name of the reference in the recorded calls</param>
    internal RecordingJsObjectReference(RecordingJsRuntime runtime, string name)
    {
        _runtime = runtime;
        Name = name;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the name of the reference in the recorded calls.
    /// </summary>
    internal string Name { get; }

    #endregion // Properties

    #region IJSObjectReference

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        return InvokeAsync<TValue>(identifier, default, args);
    }

    /// <inheritdoc />
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        _runtime.Record(new RecordedJsCall(Name, identifier, args ?? [], cancellationToken));

        if (typeof(TValue) == typeof(IJSObjectReference))
        {
            return ValueTask.FromResult((TValue)(object)_runtime.CreateReference(identifier));
        }

        return ValueTask.FromResult(default(TValue)!);
    }

    #endregion // IJSObjectReference

    #region IAsyncDisposable

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _runtime.Record(new RecordedJsCall(Name, "DisposeAsync", [], default));

        return ValueTask.CompletedTask;
    }

    #endregion // IAsyncDisposable
}