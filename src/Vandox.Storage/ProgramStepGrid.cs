using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The steps of a per-program read: multiples of the step since the Unix epoch, the first one the step that holds the start of the range.
/// </summary>
internal sealed class ProgramStepGrid
{
    #region Constants

    private const long NanosecondsPerTick = 100;

    #endregion // Constants

    #region Fields

    private readonly long _first;
    private readonly long _stepTicks;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgramStepGrid"/> class.
    /// </summary>
    /// <param name="first">The index of the first step since the epoch</param>
    /// <param name="count">The number of steps</param>
    /// <param name="stepTicks">The step in ticks</param>
    private ProgramStepGrid(long first, int count, long stepTicks)
    {
        _first = first;
        _stepTicks = stepTicks;
        Count = count;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of steps.
    /// </summary>
    internal int Count { get; }

    /// <summary>
    /// Gets the step in nanoseconds.
    /// </summary>
    internal long StepNanoseconds => _stepTicks * NanosecondsPerTick;

    /// <summary>
    /// Gets the start of the first step in nanoseconds, clamped to the range of a nanosecond count.
    /// </summary>
    internal long StartNanoseconds => Clamp((Int128)_first * StepNanoseconds);

    /// <summary>
    /// Gets the end of the last step in nanoseconds, clamped to the range of a nanosecond count.
    /// </summary>
    internal long EndNanoseconds => Clamp(((Int128)_first + Count) * StepNanoseconds);

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the grid of the steps that overlap a range.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range, after the start</param>
    /// <param name="step">The step</param>
    /// <returns>The grid</returns>
    internal static ProgramStepGrid Create(DateTimeOffset from, DateTimeOffset to, TimeSpan step)
    {
        var first = SeriesTime.FloorDiv(from.UtcTicks - SeriesTime.EpochTicks, step.Ticks);
        var count = SeriesTime.Overlapping(from.UtcTicks - SeriesTime.EpochTicks, to.UtcTicks - SeriesTime.EpochTicks, step.Ticks);

        return new ProgramStepGrid(first, (int)Math.Min(count, int.MaxValue), step.Ticks);
    }

    /// <summary>
    /// Returns the position of a step in the grid.
    /// </summary>
    /// <param name="index">The index of the step since the epoch</param>
    /// <returns>The position, or -1 when the step is not in the grid</returns>
    internal int PositionOf(long index)
    {
        var position = index - _first;

        return position >= 0 && position < Count ? (int)position : -1;
    }

    /// <summary>
    /// Returns the start times of the steps.
    /// </summary>
    /// <returns>The start times</returns>
    internal List<DateTimeOffset> Times()
    {
        var times = new List<DateTimeOffset>(Count);

        for (var position = 0; position < Count; position++)
        {
            var ticks = SeriesTime.EpochTicks + ((_first + position) * _stepTicks);

            times.Add(new DateTimeOffset(Math.Clamp(ticks, StorableTime.Min.UtcTicks, StorableTime.Max.UtcTicks), TimeSpan.Zero));
        }

        return times;
    }

    /// <summary>
    /// Clamps a count of nanoseconds to the range of an int64.
    /// </summary>
    /// <param name="value">The count</param>
    /// <returns>The clamped count</returns>
    private static long Clamp(Int128 value)
    {
        if (value < long.MinValue)
        {
            return long.MinValue;
        }

        return value > long.MaxValue ? long.MaxValue : (long)value;
    }

    #endregion // Methods
}