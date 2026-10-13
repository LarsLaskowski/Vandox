using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Components.Account;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="RedirectToLogin"/>
/// </summary>
[TestClass]
public class RedirectToLoginTests
{
    #region Methods

    /// <summary>
    /// The component navigates to the login page with a full page load.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task RedirectToLoginNavigatesWithForceLoad()
    {
        // Arrange
        var navigation = new TestNavigationManager();

        await using var services = new ServiceCollection().AddLogging().AddSingleton<NavigationManager>(navigation).BuildServiceProvider();
        await using var renderer = new ComponentTestRenderer(services, services.GetRequiredService<ILoggerFactory>());

        // Act
        await renderer.AddAsync<RedirectToLogin>(new Dictionary<string, object?>());

        // Assert
        Assert.HasCount(1, navigation.Navigations, "one navigation");
        Assert.AreEqual("login", navigation.Navigations[0].Uri, "the login page");
        Assert.IsTrue(navigation.Navigations[0].Options.ForceLoad, "a full page load");
        Assert.IsEmpty(renderer.Exceptions, "no exception in the component");
    }

    #endregion // Methods
}