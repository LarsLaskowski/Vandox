namespace Vandox.Storage.Tests;

/// <summary>
/// A clock that advances by a fixed step on every read of the timestamp, counts the reads and tells a listener the number of each read.
/// </summary>
internal sealed class CountingClock : TimeProvider
{
    #region Fields

    private readonly TimeSpan _step;

    private long _ticks;

    private int _reads;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CountingClock"/> class.
    /// </summary>
    /// <param name="step">The time every read advances the clock by</param>
    internal CountingClock(TimeSpan step)
    {
        _step = step;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of reads so far.
    /// </summary>
    internal int Reads => Volatile.Read(ref _reads);

    /// <summary>
    /// Gets or sets the action that runs on every read with its number, counted from 1.
    /// </summary>
    internal Action<int>? OnRead { get; set; }

    #endregion // Properties

    #region TimeProvider

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp()
    {
        var read = Interlocked.Increment(ref _reads);
        var ticks = Interlocked.Add(ref _ticks, _step.Ticks);

        OnRead?.Invoke(read);

        return ticks;
    }

    #endregion // TimeProvider
}