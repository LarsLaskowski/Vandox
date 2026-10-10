using System.Net;

using Vandox.Core.Configuration;
using Vandox.Core.Model;

namespace Vandox.Backend.Hosting;

/// <summary>
/// A listen address of the configuration: <c>[host]:port</c> with an empty host (all interfaces) or an IP address.
/// </summary>
/// <param name="Address">The IP address; <c>null</c> for all interfaces</param>
/// <param name="Port">The port, 1 to 65535</param>
public sealed record ListenAddress(IPAddress? Address, int Port)
{
    #region Methods

    /// <summary>
    /// Parses an address the configuration loader accepted.
    /// </summary>
    /// <param name="value">The address</param>
    /// <returns>The address and the port</returns>
    /// <exception cref="FormatException">The address is not valid</exception>
    public static ListenAddress Parse(string value)
    {
        var port = BackendConfigLoader.ParseListenPort(value);

        if (port == 0)
        {
            throw new FormatException("the listen address is not valid");
        }

        var host = value[..value.LastIndexOf(':')].Trim('[', ']');

        if (host.Length == 0)
        {
            return new ListenAddress(null, port);
        }

        return IpJson.TryParseAddress(host, out var address) && address is not null
                   ? new ListenAddress(address, port)
                   : throw new FormatException("the listen address is not valid");
    }

    #endregion // Methods
}