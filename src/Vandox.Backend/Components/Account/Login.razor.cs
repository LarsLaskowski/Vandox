using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

using Vandox.Backend.Security;

namespace Vandox.Backend.Components.Account;

/// <summary>
/// The login page.
/// </summary>
public sealed partial class Login
{
    #region Constants

    private const string HomePath = "/";

    private const string WrongPasswordMessage = "The password is not correct.";

    private const string ThrottledMessage = "Too many sign-in attempts. Try again later.";

    #endregion // Constants

    #region Fields

    private string? _message;

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the request of the page.
    /// </summary>
    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    /// <summary>
    /// Gets or sets the password of the posted form.
    /// </summary>
    [SupplyParameterFromForm(Name = "password")]
    private string? Password { get; set; }

    /// <summary>
    /// Gets or sets the navigation.
    /// </summary>
    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>
    /// Gets or sets the login service.
    /// </summary>
    [Inject]
    private LoginService LoginService { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the password of the posted form and signs in, or shows why not.
    /// </summary>
    /// <returns>A task that completes when the outcome is shown or the browser is sent to the home page</returns>
    private async Task SubmitAsync()
    {
        var context = HttpContext;

        if (context is null)
        {
            return;
        }

        var outcome = await LoginService.SignInAsync(context, Password, context.RequestAborted);

        if (outcome == LoginOutcome.SignedIn)
        {
            Navigation.NavigateTo(HomePath);

            return;
        }

        var throttled = outcome == LoginOutcome.Throttled;

        context.Response.StatusCode = throttled ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
        _message = throttled ? ThrottledMessage : WrongPasswordMessage;
    }

    #endregion // Methods

    #region ComponentBase

    /// <inheritdoc/>
    protected override void OnInitialized()
    {
        if (HttpContext?.User.Identity?.IsAuthenticated == true)
        {
            Navigation.NavigateTo(HomePath);
        }
    }

    #endregion // ComponentBase
}