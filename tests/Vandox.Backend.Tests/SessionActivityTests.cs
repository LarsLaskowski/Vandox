using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="SessionActivity"/>
/// </summary>
[TestClass]
public class SessionActivityTests
{
    #region Methods

    /// <summary>
    /// The activity is paused while any scope of a pause is open and stops being paused when its last scope is disposed.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void PauseHoldsUntilItsLastScopeIsDisposed()
    {
        // Arrange
        var activity = new SessionActivity();
        var pausedBefore = activity.IsPaused;
        var first = activity.Pause();
        var second = activity.Pause();

        // Act
        first.Dispose();

        var pausedWithOneScope = activity.IsPaused;

        second.Dispose();

        // Assert
        Assert.IsFalse(pausedBefore, "a new activity is not paused");
        Assert.IsTrue(pausedWithOneScope, "one open scope still pauses");
        Assert.IsFalse(activity.IsPaused, "no open scope, no pause");
    }

    /// <summary>
    /// A scope disposed twice releases its pause once, so it neither ends another scope's pause nor lets a later pause count from below zero.
    /// </summary>
    [TestMethod]
    [TestCategory("AC9")]
    public void PauseScopeDisposedTwiceReleasesOnce()
    {
        // Arrange
        var activity = new SessionActivity();
        var first = activity.Pause();
        var second = activity.Pause();

        // Act
        first.Dispose();
        first.Dispose();

        var pausedAfterDoubleDispose = activity.IsPaused;

        second.Dispose();
        second.Dispose();

        var pausedAfterAll = activity.IsPaused;
        var later = activity.Pause();

        // Assert
        Assert.IsTrue(pausedAfterDoubleDispose, "the second scope still pauses after the first was disposed twice");
        Assert.IsFalse(pausedAfterAll, "no pause after every scope was disposed");
        Assert.IsTrue(activity.IsPaused, "a later pause pauses again");

        later.Dispose();
    }

    #endregion // Methods
}