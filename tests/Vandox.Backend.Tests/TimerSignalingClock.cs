using Microsoft.Extensions.Time.Testing;

namespace Vandox.Backend.Tests;

/// <summary>
/// A fake clock that tells a test when the code under test has armed its timers, so the test moves the clock once, after the timers exist, instead of
/// moving it in a loop with a real delay in between.
/// </summary>
internal sealed class TimerSignalingClock : FakeTimeProvider
{
    #region Fields

    /// <summary>
    /// The lock that guards the created due times and the waiters.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The due time of every timer created on the clock.
    /// </summary>
    private readonly List<TimeSpan> _dueTimes = [];

    /// <summary>
    /// The waiters that are not complete yet.
    /// </summary>
    private readonly List<(TimeSpan DueTime, int Count, TaskCompletionSource Source)> _waiters = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TimerSignalingClock" /> class.
    /// </summary>
    internal TimerSignalingClock()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimerSignalingClock" /> class.
    /// </summary>
    /// <param name="startDateTime">The start time of the clock</param>
    internal TimerSignalingClock(DateTimeOffset startDateTime)
        : base(startDateTime)
    {
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Waits until the given number of timers with the given due time were created on the clock.
    /// </summary>
    /// <param name="dueTime">The due time the timers are created with</param>
    /// <param name="count">The number of timers</param>
    /// <param name="cancellationToken">The token that cancels the wait</param>
    /// <returns>A task that completes when the number of timers is reached</returns>
    internal Task WaitForTimersAsync(TimeSpan dueTime, int count, CancellationToken cancellationToken)
    {
        TaskCompletionSource source;

        lock (_lock)
        {
            if (_dueTimes.Count(candidate => candidate == dueTime) >= count)
            {
                return Task.CompletedTask;
            }

            source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((dueTime, count, source));
        }

        return source.Task.WaitAsync(cancellationToken);
    }

    #endregion // Methods

    #region TimeProvider

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);

        lock (_lock)
        {
            _dueTimes.Add(dueTime);

            foreach (var waiter in _waiters.Where(candidate => _dueTimes.Count(due => due == candidate.DueTime) >= candidate.Count).ToList())
            {
                _waiters.Remove(waiter);
                waiter.Source.TrySetResult();
            }
        }

        return timer;
    }

    #endregion // TimeProvider
}