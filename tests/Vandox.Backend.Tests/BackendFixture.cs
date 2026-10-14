using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Vandox.Backend.Hosting;

namespace Vandox.Backend.Tests;

/// <summary>
/// A configuration file and storage directory in a temporary folder, and helpers to run vandoxd against them.
/// </summary>
internal sealed class BackendFixture : IDisposable
{
    #region Constants

    /// <summary>
    /// The name of the variable that holds the hash of the web password.
    /// </summary>
    internal const string HashVariable = "VANDOX_WEB_PASSWORD_HASH";

    /// <summary>
    /// The web password that <see cref="HashText"/> belongs to.
    /// </summary>
    internal const string Password = "test-password-for-vandox-tests";

    /// <summary>
    /// The argon2id hash of <see cref="Password"/> at the lowest cost the service accepts (memory 19,456 KiB, 2 iterations, parallelism 1,
    /// salt <c>vandox-test-salt</c>), created with the reference form so that no test needs to compute it.
    /// </summary>
    internal const string HashText = "$argon2id$v=19$m=19456,t=2,p=1$dmFuZG94LXRlc3Qtc2FsdA$3frGsB3okaDzKgyRpN+kVpXZO4FXj6KACFWm29rDI0Y";

    #endregion // Constants

    #region Fields

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentQueue<Task> _runs = new();

    #endregion // Fields

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
        UseTrustedProxies(null);
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
    /// Writes the configuration file again, with the option <c>web.trusted_proxies</c>.
    /// </summary>
    /// <param name="trustedProxies">The value of the option; the key is left out when <c>null</c></param>
    internal void UseTrustedProxies(string? trustedProxies)
    {
        var option = trustedProxies is null ? string.Empty : $"  trusted_proxies: \"{trustedProxies}\"\n";

        File.WriteAllText(ConfigPath, $"web:\n  listen: 127.0.0.1:{WebPort}\n{option}ingest:\n  listen: 127.0.0.1:{IngestPort}\nstorage:\n  directory: {Storage}\nlog:\n  level: debug\n");
    }

    /// <summary>
    /// Runs vandoxd with this fixture's configuration file and streams and the arguments after it.
    /// </summary>
    /// <param name="arguments">The arguments</param>
    /// <param name="hooks">The hooks</param>
    /// <param name="cancellationToken">Stops the run</param>
    /// <param name="environment">The environment; the hash of <see cref="Password"/> in <see cref="HashVariable"/> when empty</param>
    /// <returns>A task that returns the exit code</returns>
    internal Task<int> RunAsync(string[] arguments, ServeHooks? hooks, CancellationToken cancellationToken, params KeyValuePair<string, string>[] environment)
    {
        var variables = environment.Length == 0 ? new[] { new KeyValuePair<string, string>(HashVariable, HashText) } : environment;
        var run = RunLinkedAsync(["-config", ConfigPath, .. arguments], variables, hooks ?? new ServeHooks(), cancellationToken);

        _runs.Enqueue(run);

        return run;
    }

    /// <summary>
    /// Starts the service with the default environment and waits until it listens.
    /// </summary>
    /// <param name="stop">Stops the service</param>
    /// <param name="wait">Stops the wait for the start</param>
    /// <param name="clock">The clock of the service, <c>null</c> for the real one</param>
    /// <returns>A task that returns the task of the run</returns>
    internal async Task<Task<int>> StartServiceAsync(CancellationToken stop, CancellationToken wait, TimeProvider? clock = null)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooks = new ServeHooks
                    {
                        Clock = clock,
                        Started = _ => started.TrySetResult()
                    };
        var run = RunAsync([], hooks, stop);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(20), wait).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// Reads the log lines the run wrote to standard error.
    /// </summary>
    /// <returns>One dictionary per line: the attribute names with their values as text</returns>
    internal List<Dictionary<string, string>> LogEntries()
    {
        var entries = new List<Dictionary<string, string>>();

        foreach (var line in Error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var attributes = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line) ?? [];

            entries.Add(attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value.ToString(), StringComparer.Ordinal));
        }

        return entries;
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

    /// <summary>
    /// Runs vandoxd until the given token or the shutdown of this fixture stops it, so that a test that fails before it stops the service does not leave it running.
    /// </summary>
    /// <param name="arguments">The arguments</param>
    /// <param name="environment">The environment</param>
    /// <param name="hooks">The hooks</param>
    /// <param name="cancellationToken">Stops the run</param>
    /// <returns>A task that returns the exit code</returns>
    private async Task<int> RunLinkedAsync(string[] arguments, KeyValuePair<string, string>[] environment, ServeHooks hooks, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

        return await BackendApp.RunAsync(arguments, environment, Out, Error, hooks, linked.Token).ConfigureAwait(false);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            Task.WaitAll(_runs.ToArray(), TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // A run that failed has been reported by its test already.
        }

        _shutdown.Dispose();
        Folder.Dispose();
        Out.Dispose();
        Error.Dispose();
    }

    #endregion // IDisposable
}