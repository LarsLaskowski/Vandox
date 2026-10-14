using Microsoft.AspNetCore.Components;

namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="NavigationManager"/> that starts at any address, records the targets of <see cref="NavigationManager.NavigateTo(string, bool, bool)"/> and applies them
/// as the new address. A page needs the router to read the new address; tests set the new parameters of the page after a navigation.
/// </summary>
internal sealed class RecordingNavigationManager : NavigationManager
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingNavigationManager"/> class.
    /// </summary>
    /// <param name="uri">The absolute address to start at, below <c>http://localhost/</c></param>
    internal RecordingNavigationManager(string uri)
    {
        Initialize("http://localhost/", uri);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the targets that were navigated to, as passed.
    /// </summary>
    internal List<string> Targets { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Sets the current address without recording a navigation.
    /// </summary>
    /// <param name="uri">The absolute or base-relative address</param>
    internal void Move(string uri)
    {
        Uri = ToAbsoluteUri(uri).ToString();
    }

    #endregion // Methods

    #region NavigationManager

    /// <inheritdoc />
    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        Targets.Add(uri);

        Uri = ToAbsoluteUri(uri).ToString();

        NotifyLocationChanged(false);
    }

    #endregion // NavigationManager
}