using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="IncidentConfig"/>
/// </summary>
[TestClass]
public class IncidentConfigTests
{
    #region Methods

    /// <summary>
    /// The defaults give 60 and 30 minutes, set values are parsed, and an unvalidated value cannot be read as a window.
    /// </summary>
    [TestMethod]
    public void IncidentConfigParsesWindows()
    {
        // Arrange
        var defaults = new IncidentConfig();
        var custom = new IncidentConfig
                     {
                         WindowBefore = "2h",
                         WindowAfter = "15m"
                     };
        var broken = new IncidentConfig
                     {
                         WindowBefore = "soon",
                         WindowAfter = "later"
                     };

        // Act
        var before = defaults.GetWindowBefore();
        var after = defaults.GetWindowAfter();
        var customBefore = custom.GetWindowBefore();
        var customAfter = custom.GetWindowAfter();
        var brokenBefore = Assert.ThrowsExactly<InvalidOperationException>(() => broken.GetWindowBefore(), "unvalidated window_before");
        var brokenAfter = Assert.ThrowsExactly<InvalidOperationException>(() => broken.GetWindowAfter(), "unvalidated window_after");

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(60), before, "default window before");
        Assert.AreEqual(TimeSpan.FromMinutes(30), after, "default window after");
        Assert.AreEqual(TimeSpan.FromHours(2), customBefore, "window before 2h");
        Assert.AreEqual(TimeSpan.FromMinutes(15), customAfter, "window after 15m");
        Assert.AreEqual("config: incidents.window_before is not valid", brokenBefore.Message, "message of window_before");
        Assert.AreEqual("config: incidents.window_after is not valid", brokenAfter.Message, "message of window_after");
    }

    #endregion // Methods
}