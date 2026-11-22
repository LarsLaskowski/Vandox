using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Components.Pages;

/// <summary>
/// Writes the address of a view as the location of the page and tells whether a location is the page, for the history view and the processes view.
/// </summary>
internal static class PageLocation
{
    #region Methods

    /// <summary>
    /// Writes an address as the location.
    /// </summary>
    /// <param name="navigation">The navigation manager</param>
    /// <param name="href">Gives the link of the address; it throws <see cref="InvalidOperationException"/> for an address it refuses</param>
    /// <param name="replace">Whether the current entry of the history is replaced</param>
    /// <returns>The text of the refusal, or an empty text when the location was written</returns>
    internal static string Go(NavigationManager navigation, Func<string> href, bool replace)
    {
        string link;

        try
        {
            link = href();
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }

        navigation.NavigateTo(link,
                              new NavigationOptions
                              {
                                  ReplaceHistoryEntry = replace
                              });

        return string.Empty;
    }

    /// <summary>
    /// Tells whether a location is a page.
    /// </summary>
    /// <param name="navigation">The navigation manager</param>
    /// <param name="location">The location</param>
    /// <param name="path">The path of the page, relative to the base address</param>
    /// <returns><c>true</c> when the location is the page</returns>
    internal static bool IsPage(NavigationManager navigation, string location, string path)
    {
        var relative = navigation.ToBaseRelativePath(location);
        var end = relative.IndexOfAny(['?', '#']);

        return string.Equals(end < 0 ? relative : relative[..end], path, StringComparison.Ordinal);
    }

    #endregion // Methods
}