using Vandox.Core.IO;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="StrictGzip"/>
/// </summary>
[TestClass]
public class StrictGzipTests
{
    #region Methods

    /// <summary>
    /// The test run has strict gzip validation on, because the build sets the runtime switch for every project.
    /// </summary>
    [TestMethod]
    public void StrictGzipRequirePassesWhenSwitchIsOn()
    {
        // Act
        StrictGzip.Require();

        // Assert
        Assert.IsTrue(AppContext.TryGetSwitch(StrictGzip.SwitchName, out var enabled) && enabled, "the switch is on");
    }

    #endregion // Methods
}