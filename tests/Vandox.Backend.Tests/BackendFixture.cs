using System.Net;
using System.Net.Sockets;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// A configuration file and storage directory in a temporary folder, and helpers to run vandoxd against them.
/// </summary>
internal sealed class BackendFixture : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BackendFixture"/> class.
    /// </summary>
    internal BackendFixture()
    {
        Folder = new TempDirectory();
        Storage = Path.Combine(Folder.Path, "data");
        Directory.CreateDirectory(Storage);
        WebPort = FreePort();
        IngestPort = FreePort();
        ConfigPath = Path.Combine(Folder.Path, "vandoxd.yaml");
        File.WriteAllText(ConfigPath, $"web:\n  listen: 127.0.0.1:{WebPort}\ningest:\n  listen: 127.0.0.1:{IngestPort}\nstorage:\n  directory: {Storage}\nlog:\n  level: debug\n");
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the temporary folder.
    /// </summary>
    internal TempDirectory Folder { get; }

    /// <summary>
    /// Gets the storage directory.
    /// </summary>
    internal string Storage { get; }

    /// <summary>
    /// Gets the path of the configuration file.
    /// </summary>
    internal string ConfigPath { get; }

    /// <summary>
    /// Gets the web port written to the configuration.
    /// </summary>
    internal int WebPort { get; }

    /// <summary>
    /// Gets the ingest port written to the configuration.
    /// </summary>
    internal int IngestPort { get; }

    /// <summary>
    /// Gets what the run wrote to standard output.
    /// </summary>
    internal StringWriter Out { get; } = new();

    /// <summary>
    /// Gets what the run wrote to standard error.
    /// </summary>
    internal StringWriter Error { get; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Runs vandoxd with this fixture's configuration file and streams and the arguments after it.
    /// </summary>
    /// <param name="arguments">The arguments</param>
    /// <param name="hooks">The hooks</param>
    /// <param name="cancellationToken">Stops the run</param>
    /// <param name="environment">The environment; empty when <c>null</c></param>
    /// <returns>A task that returns the exit code</returns>
    internal Task<int> RunAsync(string[] arguments, ServeHooks? hooks, CancellationToken cancellationToken, params KeyValuePair<string, string>[] environment)
    {
        return BackendApp.RunAsync(["-config", ConfigPath, .. arguments], environment, Out, Error, hooks ?? new ServeHooks(), cancellationToken);
    }

    /// <summary>
    /// Returns a port that is free right now.
    /// </summary>
    /// <returns>The port</returns>
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);

        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Folder.Dispose();
        Out.Dispose();
        Error.Dispose();
    }

    #endregion // IDisposable
}