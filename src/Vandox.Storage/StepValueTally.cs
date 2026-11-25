namespace Vandox.Storage;

/// <summary>
/// The values of one step: minimum, maximum, sum, count and the last value with its time.
/// </summary>
internal sealed class StepValueTally
{
    #region Fields

    private double _minimum = double.MaxValue;

    private double _maximum = double.MinValue;

    private double _sum;

    private long _count;

    private double _last;

    private DateTimeOffset _lastAt;

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Adds a value.
    /// </summary>
    /// <param name="value">The value</param>
    /// <param name="at">The capture time of the value</param>
    internal void Add(double value, DateTimeOffset at)
    {
        _minimum = Math.Min(_minimum, value);
        _maximum = Math.Max(_maximum, value);
        _sum += value;
        _count++;
        _last = value;
        _lastAt = at;
    }

    /// <summary>
    /// Builds the point of the step.
    /// </summary>
    /// <returns>The point</returns>
    internal SeriesPoint ToPoint()
    {
        return new SeriesPoint
               {
                   State = PointState.Value,
                   Minimum = _minimum,
                   Average = _sum / _count,
                   Maximum = _maximum,
                   Last = _last,
                   LastAt = _lastAt,
                   Count = _count
               };
    }

    #endregion // Methods
}