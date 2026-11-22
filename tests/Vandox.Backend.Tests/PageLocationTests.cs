using Microsoft.AspNetCore.Components;

using Vandox.Backend.Components.Pages;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="PageLocation"/>
/// </summary>
[TestClass]
public class PageLocationTests
{
    #region Methods

    /// <summary>
    /// An address the page refuses gives the text of the refusal and navigates nowhere.
    /// </summary>
    [TestMethod]
    [TestCategory("AC4")]
    public void PageLocationGoReturnsTheRefusalWithoutNavigating()
    {
        // Arrange
        var navigation = new RecordingNavigationManager("http://localhost/processes");

        // Act
        var text = PageLocation.Go(navigation, () => throw new InvalidOperationException("The address is refused."), true);

        // Assert
        Assert.AreEqual("The address is refused.", text, "the text of the refusal");
        Assert.IsEmpty(navigation.Navigations, "nothing is navigated to");
    }

    /// <summary>
    /// An accepted address is navigated to, replacing the entry of the history or not as asked.
    /// </summary>
    /// <param name="replace">Whether the entry is replaced</param>
    [TestMethod]
    [TestCategory("AC4")]
    [DataRow(true)]
    [DataRow(false)]
    public void PageLocationGoNavigatesToTheLink(bool replace)
    {
        // Arrange
        var navigation = new RecordingNavigationManager("http://localhost/processes");

        // Act
        var text = PageLocation.Go(navigation, () => "processes?range=7d", replace);

        // Assert
        Assert.AreEqual(string.Empty, text, "no refusal");
        Assert.HasCount(1, navigation.Navigations, "one navigation");
        Assert.AreEqual(replace, navigation.Navigations[0].Options.ReplaceHistoryEntry, "the entry is replaced as asked");
    }

    /// <summary>
    /// A location is the page when its path is the page, with or without a query or a fragment.
    /// </summary>
    /// <param name="location">The location</param>
    /// <param name="expected">Whether it is the page</param>
    [TestMethod]
    [TestCategory("AC10")]
    [DataRow("http://localhost/processes", true)]
    [DataRow("http://localhost/processes?source=a", true)]
    [DataRow("http://localhost/processes#top", true)]
    [DataRow("http://localhost/history?source=a", false)]
    [DataRow("http://localhost/processes/more", false)]
    public void PageLocationIsPageComparesThePathOnly(string location, bool expected)
    {
        // Arrange
        var navigation = new RecordingNavigationManager("http://localhost/processes");

        // Act
        var result = PageLocation.IsPage(navigation, location, "processes");

        // Assert
        Assert.AreEqual(expected, result, $"the location {location} is the page: {expected}");
    }

    #endregion // Methods
}