#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

using NodaTime;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Turns year-less local times of one file into UTC instants.
/// </summary>
internal sealed class SyslogClock
{
    #region Fields

    /// <summary>
    /// How far back a local time in the repeated hour may step and still take the earlier offset.
    /// </summary>
    internal static readonly TimeSpan BackwardTolerance = TimeSpan.FromMinutes(10);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SyslogClock"/> class.
    /// </summary>
    /// <param name="timeZone">The zone of the file</param>
    /// <param name="file">The file</param>
    internal SyslogClock(DateTimeZone timeZone, LogFile file)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether a name date or modification time inside the storable range exists.
    /// </summary>
    internal bool HasAnchor => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Resolves a year-less time to a UTC instant; never throws.
    /// </summary>
    /// <param name="time">The time</param>
    /// <param name="instant">The instant on success</param>
    /// <returns><c>null</c> on success, else "invalid date", "time outside the storable range" or "year unknown: the file has no usable date"</returns>
    internal string? Resolve(SyslogTime time, out DateTimeOffset instant)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}