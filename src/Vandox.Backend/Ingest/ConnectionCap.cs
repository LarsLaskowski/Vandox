using Microsoft.AspNetCore.Connections;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Limits the open connections of a listener: a connection over the limit is aborted before Kestrel reads a request.
/// </summary>
internal sealed class ConnectionCap
{
    #region Fields

    /// <summary>
    /// The most connections.
    /// </summary>
    private readonly int _max;

    /// <summary>
    /// The number of connections that are open.
    /// </summary>
    private int _open;

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
    /// Wraps the rest of the connection pipeline with the count.
    /// </summary>
    /// <param name="next">The rest of the connection pipeline</param>
    /// <returns>The wrapped delegate</returns>
    internal ConnectionDelegate Wrap(ConnectionDelegate next)
    {
        return async context =>
               {
                   if (Interlocked.Increment(ref _open) > _max)
                   {
                       Interlocked.Decrement(ref _open);
                       context.Abort();

                       return;
                   }

                   try
                   {
                       await next(context).ConfigureAwait(false);
                   }
                   finally
                   {
                       Interlocked.Decrement(ref _open);
                   }
               };
    }

    #endregion // Methods
}