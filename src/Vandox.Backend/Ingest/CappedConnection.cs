using Microsoft.AspNetCore.Connections;

namespace Vandox.Backend.Ingest;

/// <summary>
/// One held connection of a <see cref="ConnectionCap"/>.
/// </summary>
#pragma warning disable RH2003, S2325
internal sealed class CappedConnection
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="CappedConnection"/> class.
    /// </summary>
    /// <param name="connection">The held connection</param>
    internal CappedConnection(ConnectionContext connection)
    {
        Connection = connection;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the held connection.
    /// </summary>
    internal ConnectionContext Connection { get; }

    /// <summary>
    /// Gets a value indicating whether a request of the connection passed the token check.
    /// </summary>
    internal bool Trusted => throw new NotImplementedException();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Marks the connection as trusted.
    /// </summary>
    internal void Trust()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}