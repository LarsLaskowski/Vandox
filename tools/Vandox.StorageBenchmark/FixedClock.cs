using System.Diagnostics.CodeAnalysis;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.StorageBenchmark;

/// <summary>
/// A clock that stays at one time.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FixedClock : TimeProvider
{
    #region Fields

    private readonly DateTimeOffset _now;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FixedClock"/> class.
    /// </summary>
    /// <param name="now">The time of the clock</param>
    public FixedClock(DateTimeOffset now)
    {
        _now = now;
    }

    #endregion // Constructors

    #region TimeProvider

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    #endregion // TimeProvider
}