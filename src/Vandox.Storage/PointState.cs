namespace Vandox.Storage;

/// <summary>
/// The state of a point of a line.
/// </summary>
public enum PointState
{
    /// <summary>
    /// The point holds a value.
    /// </summary>
    Value,

    /// <summary>
    /// The span has no data.
    /// </summary>
    Gap,

    /// <summary>
    /// The line has no sample here, though another line has one.
    /// </summary>
    BetweenSamples
}