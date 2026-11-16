using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Tests;

/// <summary>
/// A dispatcher that runs the default dispatcher and lets a test act once at the moment a component hands a work item without a result to it,
/// after the component's own checks and before the work item runs.
/// </summary>
internal sealed class HookingDispatcher : Dispatcher
{
    #region Fields

    /// <summary>
    /// The dispatcher that runs the work items.
    /// </summary>
    private readonly Dispatcher _inner = CreateDefault();

    /// <summary>
    /// The action to run before the next work item without a result, or <c>null</c> for none.
    /// </summary>
    private Action? _beforeAction;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Sets the action that runs once, on the calling thread, before the next work item without a result is handed to the dispatcher.
    /// </summary>
    /// <param name="action">The action</param>
    internal void BeforeNextAction(Action action)
    {
        Volatile.Write(ref _beforeAction, action);
    }

    #endregion // Methods

    #region Dispatcher

    /// <inheritdoc/>
    public override bool CheckAccess()
    {
        return _inner.CheckAccess();
    }

    /// <inheritdoc/>
    public override Task InvokeAsync(Action workItem)
    {
        Interlocked.Exchange(ref _beforeAction, null)?.Invoke();

        return _inner.InvokeAsync(workItem);
    }

    /// <inheritdoc/>
    public override Task InvokeAsync(Func<Task> workItem)
    {
        return _inner.InvokeAsync(workItem);
    }

    /// <inheritdoc/>
    public override Task<TResult> InvokeAsync<TResult>(Func<TResult> workItem)
    {
        return _inner.InvokeAsync(workItem);
    }

    /// <inheritdoc/>
    public override Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> workItem)
    {
        return _inner.InvokeAsync(workItem);
    }

    #endregion // Dispatcher
}