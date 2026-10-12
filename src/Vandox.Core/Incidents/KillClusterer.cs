using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

/// <summary>
/// Groups OOM kills, added in time order, into clusters whose windows overlap.
/// </summary>
public sealed class KillClusterer
{
    #region Fields

    private readonly TimeSpan _before;
    private readonly TimeSpan _after;
    private KillCluster? _open;
    private DateTimeOffset _previous = DateTimeOffset.MinValue;
    private bool _complete;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="KillClusterer"/> class.
    /// </summary>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    /// <exception cref="ArgumentOutOfRangeException">A window is not positive</exception>
    public KillClusterer(TimeSpan before, TimeSpan after)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(before, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(after, TimeSpan.Zero);

        _before = before;
        _after = after;
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
        ArgumentNullException.ThrowIfNull(kill);

        if (kill.SignatureId != IncidentSignatures.OomKill)
        {
            throw new ArgumentException("incidents: the event is not an OOM kill", nameof(kill));
        }

        if (_complete)
        {
            throw new InvalidOperationException("incidents: already complete");
        }

        if (kill.CapturedAt < _previous)
        {
            throw new InvalidOperationException("incidents: kills must be added in time order");
        }

        if (kill.CapturedAt.UtcTicks >= StorableTime.Max.UtcTicks)
        {
            return null;
        }

        _previous = kill.CapturedAt;

        var start = Max(StorableTime.Min, kill.CapturedAt - _before);
        var end = Min(StorableTime.Max, kill.CapturedAt + _after);

        if (_open is not null && start < _open.End)
        {
            Extend(_open, kill, end);

            return null;
        }

        var closed = _open;

        _open = new KillCluster
                {
                    Start = start,
                    End = end,
                    FirstKillAt = kill.CapturedAt
                };
        Extend(_open, kill, end);

        return closed;
    }

    /// <summary>
    /// Completes the clustering.
    /// </summary>
    /// <returns>The open cluster, or <c>null</c> without one</returns>
    /// <exception cref="InvalidOperationException">The clusterer is already complete</exception>
    public KillCluster? Complete()
    {
        if (_complete)
        {
            throw new InvalidOperationException("incidents: already complete");
        }

        _complete = true;

        return _open;
    }

    /// <summary>
    /// Adds a kill to a cluster.
    /// </summary>
    /// <param name="cluster">The cluster</param>
    /// <param name="kill">The kill</param>
    /// <param name="end">The end of the kill's window</param>
    private static void Extend(KillCluster cluster, DetectedEvent kill, DateTimeOffset end)
    {
        cluster.End = end;
        cluster.LastKillAt = kill.CapturedAt;
        cluster.KillCount++;

        if (cluster.Kills.Count < IncidentLimits.MaxKills)
        {
            cluster.Kills.Add(IncidentKill.From(kill));
        }
    }

    /// <summary>
    /// Returns the later of two instants.
    /// </summary>
    /// <param name="left">The first instant</param>
    /// <param name="right">The second instant</param>
    /// <returns>The later instant</returns>
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
    {
        return left > right ? left : right;
    }

    /// <summary>
    /// Returns the earlier of two instants.
    /// </summary>
    /// <param name="left">The first instant</param>
    /// <param name="right">The second instant</param>
    /// <returns>The earlier instant</returns>
    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
    {
        return left < right ? left : right;
    }

    #endregion // Methods
}