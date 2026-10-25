using System.IO.Pipelines;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;

namespace Vandox.Backend.Tests;

/// <summary>
/// A connection of a test that records whether the server aborted it.
/// </summary>
internal sealed class AbortRecordingConnection : ConnectionContext
{
    #region Fields

    private int _aborts;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of times the connection was aborted.
    /// </summary>
    internal int Aborts => Volatile.Read(ref _aborts);

    #endregion // Properties

    #region BaseConnectionContext

    /// <inheritdoc />
    public override string ConnectionId { get; set; } = Guid.NewGuid().ToString();

    /// <inheritdoc />
    public override IFeatureCollection Features { get; } = new FeatureCollection();

    /// <inheritdoc />
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

    /// <inheritdoc />
    public override void Abort(ConnectionAbortedException abortReason)
    {
        Interlocked.Increment(ref _aborts);
    }

    /// <inheritdoc />
    public override void Abort()
    {
        Interlocked.Increment(ref _aborts);
    }

    #endregion // BaseConnectionContext

    #region ConnectionContext

    /// <inheritdoc />
    public override IDuplexPipe Transport { get; set; } = null!;

    #endregion // ConnectionContext
}