using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Vandox.Backend.Overview;

// Skeleton: removed with the implementation
#pragma warning disable RH2003, S2325, S4487
/// <summary>
/// Tells whether the connection of a circuit is up and raises an event when it is restored.
/// </summary>
public sealed class CircuitConnection : CircuitHandler
{
    #region Fields

    /// <summary>
    /// The handlers of <see cref="Restored"/>.
    /// </summary>
    private EventHandler? _restored;

    #endregion // Fields

    #region Events

    /// <summary>
    /// Occurs when the connection is up again after it was down.
    /// </summary>
    public event EventHandler? Restored
    {
        add => _restored += value;
        remove => _restored -= value;
    }

    #endregion // Events

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the connection is up.
    /// </summary>
    public bool IsConnected { get; } = true;

    #endregion // Properties

    #region CircuitHandler

    /// <inheritdoc/>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // CircuitHandler
}
#pragma warning restore RH2003, S2325, S4487