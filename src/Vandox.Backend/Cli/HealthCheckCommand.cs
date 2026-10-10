using System.Net;
using System.Net.Sockets;

using Vandox.Backend.Hosting;
using Vandox.Core.Configuration;
using Vandox.Core.Model;

namespace Vandox.Backend.Cli;

/// <summary>
/// The image's health probe: <c>vandoxd -healthcheck</c> asks <c>/healthz</c> of the web listener on loopback. It loads the
/// configuration without the environment, so it reads no secret, and it uses a 4 second timeout, no proxy, no redirects and no
/// credentials.
/// </summary>
internal static class HealthCheckCommand
{
    #region Constants

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(4);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Probes <c>/healthz</c> of the web listener configured in <paramref name="configPath"/>.
    /// </summary>
    /// <param name="configPath">The path of the configuration file</param>
    /// <param name="stderr">Receives the failure text</param>
    /// <param name="handler">The HTTP handler; <c>null</c> for the real one (tests pass a double)</param>
    /// <param name="cancellationToken">Cancels the probe</param>
    /// <returns>A task that returns the exit code</returns>
    internal static async Task<int> RunAsync(string configPath, TextWriter stderr, HttpMessageHandler? handler, CancellationToken cancellationToken)
    {
        try
        {
            var config = BackendConfigLoader.Load(configPath, []);
            var target = HealthUrl(config.Web.Listen);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            deadline.CancelAfter(_timeout);

            using var client = new HttpClient(handler ?? Handler(), disposeHandler: handler is null)
                               {
                                   Timeout = _timeout
                               };
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return 0;
            }

            await stderr.WriteLineAsync($"vandoxd: health check failed: unexpected status {(int)response.StatusCode}").ConfigureAwait(false);

            return 1;
        }
        catch (Exception exception) when (exception is ConfigException or HttpRequestException or OperationCanceledException or FormatException)
        {
            await stderr.WriteLineAsync($"vandoxd: health check failed: {Reason(exception)}").ConfigureAwait(false);

            return 1;
        }
    }

    /// <summary>
    /// Returns the <c>/healthz</c> URL for a web listen address, using loopback for an empty or unspecified host.
    /// </summary>
    /// <param name="listen">The listen address as <c>[host]:port</c></param>
    /// <returns>The URL</returns>
    internal static string HealthUrl(string listen)
    {
        var address = ListenAddress.Parse(listen);
        var host = address.Address;

        if (host is not null && host.IsIPv4MappedToIPv6)
        {
            host = host.MapToIPv4();
        }

        if (host is null || host.Equals(IPAddress.Any))
        {
            host = IPAddress.Loopback;
        }
        else if (host.Equals(IPAddress.IPv6Any))
        {
            host = IPAddress.IPv6Loopback;
        }

        var text = host.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host.ToString();

#pragma warning disable S5332 // The probe talks to the service on loopback inside the container; there is no certificate and no secret on this path.
        return $"http://{text}:{address.Port}/healthz";
#pragma warning restore S5332
    }

    /// <summary>
    /// Returns the HTTP handler of the probe: no proxy, no redirects, no connection reuse.
    /// </summary>
    /// <returns>The handler</returns>
    private static SocketsHttpHandler Handler()
    {
        return new SocketsHttpHandler
               {
                   UseProxy = false,
                   AllowAutoRedirect = false,
                   PooledConnectionLifetime = TimeSpan.Zero,
                   ConnectTimeout = _timeout
               };
    }

    /// <summary>
    /// Returns the text of a failure for the error output.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns>The text</returns>
    private static string Reason(Exception exception)
    {
        return exception switch
               {
                   ConfigException => exception.Message,
                   OperationCanceledException => "timed out",
                   _ => exception.Message
               };
    }

    #endregion // Methods
}