using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vandox.Backend.Overview;

/// <summary>
/// Tells whether the connection of a circuit is up and raises an event when it is restored.
/// </summary>
public sealed class CircuitConnection : CircuitHandler
{
    #region Fields

    /// <summary>
    /// Whether the connection is up.
    /// </summary>
    private volatile bool _isConnected = true;

    #endregion // Fields

    #region Events

    /// <summary>
    /// Occurs when the connection is up again after it was down.
    /// </summary>
    public event EventHandler? Restored;

    #endregion // Events

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the connection is up; it is up before the first connection, so a prerendered page counts as connected.
    /// </summary>
    public bool IsConnected => _isConnected;

    #endregion // Properties

    #region CircuitHandler

    /// <inheritdoc/>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        _isConnected = false;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var wasDown = _isConnected == false;

        _isConnected = true;

        if (wasDown)
        {
            Restored?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    #endregion // CircuitHandler
}