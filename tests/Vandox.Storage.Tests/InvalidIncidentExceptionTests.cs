namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="InvalidIncidentException"/>
/// </summary>
[TestClass]
public class InvalidIncidentExceptionTests
{
    #region Methods

    /// <summary>
    /// The exception keeps its message and, when one is given, the cause; it is a store error.
    /// </summary>
    [TestMethod]
    public void InvalidIncidentExceptionKeepsMessageAndCause()
    {
        // Arrange
        var cause = new InvalidOperationException("cause");

        // Act
        var plain = new InvalidIncidentException("store: invalid incident: plain");
        var wrapped = new InvalidIncidentException("store: invalid incident: wrapped", cause);

        // Assert
        Assert.AreEqual("store: invalid incident: plain", plain.Message, "message without a cause");
        Assert.IsNull(plain.InnerException, "no cause");
        Assert.AreEqual("store: invalid incident: wrapped", wrapped.Message, "message with a cause");
        Assert.AreSame(cause, wrapped.InnerException, "cause");
        Assert.IsInstanceOfType<StoreException>(wrapped, "a store error");
    }

    #endregion // Methods
}