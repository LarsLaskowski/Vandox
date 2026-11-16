using Vandox.Backend.Overview;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="CircuitConnection"/>
/// </summary>
[TestClass]
public class CircuitConnectionTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A connection that goes down is not connected, and when it comes back it is connected again and raises <see cref="CircuitConnection.Restored"/> once.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task CircuitConnectionRaisesRestoredWhenConnectionComesBack()
    {
        // Arrange
        var connection = new CircuitConnection();
        var restored = 0;

        connection.Restored += (_, _) => restored++;

        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);
        await connection.OnConnectionDownAsync(null!, TestContext.CancellationToken);

        var connectedWhileDown = connection.IsConnected;
        var restoredWhileDown = restored;

        // Act
        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);

        // Assert
        Assert.IsFalse(connectedWhileDown, "the connection is not connected while it is down");
        Assert.AreEqual(0, restoredWhileDown, "nothing is raised while the connection goes down");
        Assert.IsTrue(connection.IsConnected, "the connection is connected again");
        Assert.AreEqual(1, restored, "Restored is raised once when the connection comes back");
    }

    /// <summary>
    /// The first connection of a circuit, which comes up without a prior down, raises nothing and leaves the connection connected.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC7")]
    public async Task CircuitConnectionRaisesNothingOnFirstConnection()
    {
        // Arrange
        var connection = new CircuitConnection();
        var restored = 0;

        connection.Restored += (_, _) => restored++;

        // Act
        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);
        await connection.OnConnectionUpAsync(null!, TestContext.CancellationToken);

        // Assert
        Assert.IsTrue(connection.IsConnected, "the connection is connected");
        Assert.AreEqual(0, restored, "an up without a prior down raises nothing");
    }

    #endregion // Methods
}