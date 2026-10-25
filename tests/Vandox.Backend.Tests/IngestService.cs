using System.Net.Http.Headers;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Vandox.Backend.Hosting;
using Vandox.Backend.Ingest;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// The real service with ingest switched on, started in a <see cref="BackendFixture"/>, for the tests of the shutdown and of the listener. A test can put a
/// recording (and holding) writer in front of the real store and a scripted free space into the endpoint of the running service.
/// </summary>
internal sealed class IngestService : IAsyncDisposable
{
    #region Constants

    /// <summary>
    /// The token of the service.
    /// </summary>
    internal const string Token = "serve-token-0123456789-abcdefghijklmnopqrst";

    #endregion // Constants

    #region Fields

    private readonly CancellationTokenSource _stop = new();

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly HttpClient _client = new(new HttpClientHandler
                                              {
                                                  AllowAutoRedirect = false
                                              })
                                          {
                                              Timeout = TimeSpan.FromSeconds(60)
                                          };

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestService"/> class.
    /// </summary>
    private IngestService()
    {
        Fixture = new BackendFixture();
        Run = Task.FromResult(-1);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the fixture with the configuration and the storage directory.
    /// </summary>
    internal BackendFixture Fixture { get; }

    /// <summary>
    /// Gets the task of the run, which returns the exit code.
    /// </summary>
    internal Task<int> Run { get; private set; }

    /// <summary>
    /// Gets the recording writer in front of the store, <c>null</c> when the test asked for none.
    /// </summary>
    internal RecordingRecordWriter? Writer { get; private set; }

    /// <summary>
    /// Gets a task that completes when the stop signal of the host fired.
    /// </summary>
    internal Task Stopping => _stopping.Task;

    /// <summary>
    /// Gets a task that completes when the host stopped.
    /// </summary>
    internal Task Stopped => _stopped.Task;

    /// <summary>
    /// Gets the base address of the ingest listener.
    /// </summary>
    internal string IngestUrl => $"http://127.0.0.1:{Fixture.IngestPort}";

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Starts the service and waits until it listens.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait</param>
    /// <param name="tokenMissing">Whether the agent token is left out</param>
    /// <param name="agentMissing">Whether <c>ingest.agent_id</c> is left out (it is set to <c>agent-1</c> otherwise)</param>
    /// <param name="minFree">The value of <c>ingest.min_free</c></param>
    /// <param name="record">Whether a recording writer sits in front of the store</param>
    /// <param name="hold">Whether the recording writer holds every write until the test releases it</param>
    /// <param name="clock">The clock of the service, <c>null</c> for the real one</param>
    /// <param name="freeSpace">The free space the endpoint uses, <c>null</c> for the real one</param>
    /// <param name="shutdownTimeout">The shutdown timeout of the host, <c>null</c> for the service's own</param>
    /// <param name="onStarted">Runs on the thread that announces the start, after the test knows the service listens</param>
    /// <returns>The service</returns>
    internal static async Task<IngestService> StartAsync(CancellationToken cancellationToken, bool tokenMissing = false, bool agentMissing = false, string minFree = "0MiB", bool record = false, bool hold = false, FakeTimeProvider? clock = null, FreeSpace? freeSpace = null, TimeSpan? shutdownTimeout = null, Action? onStarted = null)
    {
        var service = new IngestService();

        try
        {
            await service.BeginAsync(tokenMissing, agentMissing, minFree, record || hold, hold, clock, freeSpace, shutdownTimeout, onStarted, cancellationToken);

            return service;
        }
        catch
        {
            await service.DisposeAsync();

            throw;
        }
    }

    /// <summary>
    /// Posts a batch to the ingest listener.
    /// </summary>
    /// <param name="body">The compressed batch</param>
    /// <param name="cancellationToken">Cancels the request</param>
    /// <param name="token">The token, <c>null</c> for none</param>
    /// <returns>The response</returns>
    internal async Task<HttpResponseMessage> PostAsync(byte[] body, CancellationToken cancellationToken, string? token = Token)
    {
        using var content = new ByteArrayContent(body);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{IngestUrl}/v1/batches")
                            {
                                Content = content
                            };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Stops the service as the stop signal of the host does.
    /// </summary>
    internal void Stop()
    {
        _stop.Cancel();
    }

    /// <summary>
    /// Reads the records in the database of the service.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The rows</returns>
    internal Task<List<StoredRow>> RowsAsync(CancellationToken cancellationToken)
    {
        return IngestRig.ReadRowsAsync(Fixture.Storage, cancellationToken);
    }

    /// <summary>
    /// Replaces a field of the running endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint</param>
    /// <param name="name">The name of the field</param>
    /// <param name="value">The new value</param>
    private static void Replace(IngestEndpoint endpoint, string name, object value)
    {
        var field = typeof(IngestEndpoint).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"the endpoint has no field {name}");

        field.SetValue(endpoint, value);
    }

    /// <summary>
    /// Writes the configuration, starts the service and waits until it listens.
    /// </summary>
    /// <param name="tokenMissing">Whether the agent token is left out</param>
    /// <param name="agentMissing">Whether <c>ingest.agent_id</c> is left out</param>
    /// <param name="minFree">The value of <c>ingest.min_free</c></param>
    /// <param name="record">Whether a recording writer sits in front of the store</param>
    /// <param name="hold">Whether the recording writer holds every write</param>
    /// <param name="clock">The clock of the service, <c>null</c> for the real one</param>
    /// <param name="freeSpace">The free space the endpoint uses, <c>null</c> for the real one</param>
    /// <param name="shutdownTimeout">The shutdown timeout of the host, <c>null</c> for the service's own</param>
    /// <param name="onStarted">Runs when the start is announced</param>
    /// <param name="cancellationToken">Stops the wait</param>
    /// <returns>A task that completes when the service listens</returns>
    private async Task BeginAsync(bool tokenMissing, bool agentMissing, string minFree, bool record, bool hold, FakeTimeProvider? clock, FreeSpace? freeSpace, TimeSpan? shutdownTimeout, Action? onStarted, CancellationToken cancellationToken)
    {
        var agent = agentMissing ? string.Empty : $"  agent_id: {IngestSamples.AgentId}\n";

        await File.WriteAllTextAsync(Fixture.ConfigPath, $"web:\n  listen: 127.0.0.1:{Fixture.WebPort}\ningest:\n  listen: 127.0.0.1:{Fixture.IngestPort}\n{agent}  min_free: {minFree}\nstorage:\n  directory: {Fixture.Storage}\nlog:\n  level: debug\n", cancellationToken);

        var hooks = new ServeHooks
                    {
                        Clock = clock,
                        Built = provider => Prepare(provider, record, hold, freeSpace, shutdownTimeout),
                        Started = _ =>
                                  {
                                      _started.TrySetResult();
                                      onStarted?.Invoke();
                                  }
                    };
        var hash = new KeyValuePair<string, string>(BackendFixture.HashVariable, BackendFixture.HashText);
        var token = new KeyValuePair<string, string>(ConfigConstants.EnvAgentToken, Token);
        KeyValuePair<string, string>[] variables = tokenMissing ? [hash] : [hash, token];

        Run = Task.Run(() => Fixture.RunAsync([], hooks, _stop.Token, variables), CancellationToken.None);

        await Task.WhenAny(_started.Task, Run).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        if (_started.Task.IsCompleted)
        {
            return;
        }

        throw new InvalidOperationException($"the service stopped before it started, exit code {await Run}");
    }

    /// <summary>
    /// Hooks the running service after it was built: observes the stop signal and puts the test doubles into the endpoint.
    /// </summary>
    /// <param name="provider">The services of the application</param>
    /// <param name="record">Whether a recording writer sits in front of the store</param>
    /// <param name="hold">Whether the recording writer holds every write</param>
    /// <param name="freeSpace">The free space the endpoint uses, <c>null</c> for the real one</param>
    /// <param name="shutdownTimeout">The shutdown timeout of the host, <c>null</c> for the service's own</param>
    private void Prepare(IServiceProvider provider, bool record, bool hold, FreeSpace? freeSpace, TimeSpan? shutdownTimeout)
    {
        var lifetime = provider.GetRequiredService<IHostApplicationLifetime>();

        lifetime.ApplicationStopping.Register(() => _stopping.TrySetResult());
        lifetime.ApplicationStopped.Register(() => _stopped.TrySetResult());

        var endpoint = provider.GetRequiredService<IngestEndpoint>();

        if (record)
        {
            Writer = new RecordingRecordWriter(provider.GetRequiredService<IRecordWriter>());

            if (hold)
            {
                Writer.Hold();
            }

            Replace(endpoint, "_writer", Writer);
        }

        if (freeSpace is not null)
        {
            Replace(endpoint, "_freeSpace", freeSpace);
        }

        if (shutdownTimeout is not null)
        {
            provider.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout = shutdownTimeout.Value;
        }
    }

    #endregion // Methods

    #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Writer?.ReleaseAll();
        await _stop.CancelAsync();

        try
        {
            await Run.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // A run that did not end in time is reported by the fixture's own wait.
        }

        Writer?.Dispose();
        _client.Dispose();
        Fixture.Dispose();
        _stop.Dispose();
    }

    #endregion // IAsyncDisposable
}