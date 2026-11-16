using Microsoft.Extensions.Time.Testing;

namespace Vandox.Backend.Tests;

/// <summary>
/// A fake clock whose timers fail when they are disposed asynchronously, so the disposal of a component that awaits it throws.
/// </summary>
internal sealed class DisposeFailingClock : FakeTimeProvider
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DisposeFailingClock"/> class.
    /// </summary>
    /// <param name="startDateTime">The start time</param>
    internal DisposeFailingClock(DateTimeOffset startDateTime)
        : base(startDateTime)
    {
    }

    #endregion // Constructors

    #region TimeProvider

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        return new DisposeFailingTimer(base.CreateTimer(callback, state, dueTime, period));
    }

    #endregion // TimeProvider
}