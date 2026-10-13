using System.Net;

using Microsoft.AspNetCore.Builder;

namespace Vandox.Backend.Security;

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
        if (trustedProxies.Count == 0)
        {
            return null;
        }

        var options = new ForwardedHeadersOptions
                      {
                          ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
                          ForwardLimit = 1
                      };

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var network in trustedProxies)
        {
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }

    #endregion // Methods
}