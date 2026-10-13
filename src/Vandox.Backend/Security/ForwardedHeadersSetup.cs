using System.Net;

using Microsoft.AspNetCore.Builder;

namespace Vandox.Backend.Security;

#pragma warning disable RH2003, S2325

/// <summary>
/// Builds the options of the forwarded-headers middleware from the trusted proxies.
/// </summary>
internal static class ForwardedHeadersSetup
{
    #region Methods

    /// <summary>
    /// Creates the options that trust exactly the given networks.
    /// </summary>
    /// <param name="trustedProxies">The trusted networks</param>
    /// <returns>The options; <c>null</c> when the list is empty, so no middleware is added</returns>
    internal static ForwardedHeadersOptions? CreateOptions(IReadOnlyList<IPNetwork> trustedProxies)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}