using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// Groups OOM kills, added in time order, into clusters whose windows overlap.
/// </summary>
public sealed class KillClusterer
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="KillClusterer"/> class.
    /// </summary>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    /// <exception cref="ArgumentOutOfRangeException">A window is not positive</exception>
    public KillClusterer(TimeSpan before, TimeSpan after)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a kill.
    /// </summary>
    /// <param name="kill">The kill, in time order</param>
    /// <returns>The cluster this kill closed, or <c>null</c></returns>
    /// <exception cref="ArgumentException">The event is not an OOM kill</exception>
    /// <exception cref="InvalidOperationException">The kill is earlier than the previous one, or the clusterer is complete</exception>
    public KillCluster? Add(DetectedEvent kill)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Completes the clustering.
    /// </summary>
    /// <returns>The open cluster, or <c>null</c> without one</returns>
    /// <exception cref="InvalidOperationException">The clusterer is already complete</exception>
    public KillCluster? Complete()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}