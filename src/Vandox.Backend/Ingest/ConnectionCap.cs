using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Limits the open connections of a listener: when it is full, a new connection evicts the oldest one that has not passed the token check, and is aborted itself only when all held connections have.
/// </summary>
internal sealed class ConnectionCap
{
    #region Constants

    /// <summary>
    /// The name of the connection item that holds the <see cref="CappedConnection"/>.
    /// </summary>
    internal const string ItemName = "vandox.ingest.connection";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The most connections.
    /// </summary>
    private readonly int _max;

    /// <summary>
    /// The lock of the held connections.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The held connections in arrival order.
    /// </summary>
    private readonly List<CappedConnection> _held = [];

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionCap"/> class.
    /// </summary>
    /// <param name="max">The most connections</param>
    internal ConnectionCap(int max)
    {
        _max = max;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Marks the connection of a request as trusted: a request on it passed the token check.
    /// </summary>
    /// <param name="context">The request context</param>
    internal static void Trust(HttpContext context)
    {
        var items = context.Features.Get<IConnectionItemsFeature>()?.Items;

        if (items is not null && items.TryGetValue(ItemName, out var item) && item is CappedConnection connection)
        {
            connection.Trust();
        }
    }

    /// <summary>
    /// Wraps the rest of the connection pipeline with the cap.
    /// </summary>
    /// <param name="next">The rest of the connection pipeline</param>
    /// <returns>The wrapped delegate</returns>
    internal ConnectionDelegate Wrap(ConnectionDelegate next)
    {
        return async context =>
               {
                   var entry = new CappedConnection(context);

                   context.Items[ItemName] = entry;

                   var admitted = Admit(entry, out var victim);

                   victim?.Connection.Abort();

                   if (admitted)
                   {
                       try
                       {
                           await next(context).ConfigureAwait(false);
                       }
                       finally
                       {
                           lock (_lock)
                           {
                               _held.Remove(entry);
                           }
                       }
                   }
                   else
                   {
                       context.Abort();
                   }
               };
    }

    /// <summary>
    /// Finds the oldest held connection that has not passed the token check.
    /// </summary>
    /// <returns>The connection, or <c>null</c> when all are trusted</returns>
    private CappedConnection? FindOldestUntrusted()
    {
        foreach (var held in _held)
        {
            if (held.Trusted)
            {
                continue;
            }

            return held;
        }

        return null;
    }

    /// <summary>
    /// Admits a connection, evicting the oldest untrusted one when the cap is full.
    /// </summary>
    /// <param name="entry">The new connection</param>
    /// <param name="victim">The evicted connection, if any</param>
    /// <returns><c>true</c> when the connection is held</returns>
    private bool Admit(CappedConnection entry, out CappedConnection? victim)
    {
        victim = null;

        lock (_lock)
        {
            if (_held.Count >= _max)
            {
                victim = FindOldestUntrusted();

                if (victim is null)
                {
                    return false;
                }

                _held.Remove(victim);
            }

            _held.Add(entry);

            return true;
        }
    }

    #endregion // Methods
}