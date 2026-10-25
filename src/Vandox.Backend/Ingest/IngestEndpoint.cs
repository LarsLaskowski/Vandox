using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vandox.Storage;

namespace Vandox.Backend.Ingest;

#pragma warning disable RH2003, S2325, S4487

/// <summary>
/// Answers <c>POST /v1/batches</c> on the ingest listener: checks the token and the agent, decodes the batch, writes it and answers
/// 204 only after the commit.
/// </summary>
internal sealed class IngestEndpoint : IDisposable
{
    #region Fields

    /// <summary>
    /// The settings; <c>null</c> when ingest is disabled.
    /// </summary>
    private readonly IngestSettings? _settings;

    /// <summary>
    /// Stores the batches.
    /// </summary>
    private readonly IRecordWriter _writer;

    /// <summary>
    /// Reads the free space of the storage volume.
    /// </summary>
    private readonly FreeSpace _freeSpace;

    /// <summary>
    /// Signals the stop of the application.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    /// <summary>
    /// The clock.
    /// </summary>
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates the loggers.
    /// </summary>
    private readonly ILoggerFactory _loggerFactory;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestEndpoint"/> class.
    /// </summary>
    /// <param name="settings">The settings; <c>null</c> when ingest is disabled</param>
    /// <param name="writer">Stores the batches</param>
    /// <param name="freeSpace">Reads the free space of the storage volume</param>
    /// <param name="lifetime">Signals the stop of the application</param>
    /// <param name="clock">The clock</param>
    /// <param name="loggerFactory">Creates the loggers</param>
    internal IngestEndpoint(IngestSettings? settings, IRecordWriter writer, FreeSpace freeSpace, IHostApplicationLifetime lifetime, TimeProvider clock, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _writer = writer;
        _freeSpace = freeSpace;
        _lifetime = lifetime;
        _clock = clock;
        _loggerFactory = loggerFactory;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Answers one ingest request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the answer is set</returns>
    internal Task HandleAsync(HttpContext context)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Closes the write gate and waits for the writes in flight, at most until the shutdown token is cancelled (10 seconds after the stop
    /// signal) and the cancelled writes have rolled back.
    /// </summary>
    /// <returns>A task that completes when no write is in flight</returns>
    internal Task DrainAsync()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        // The skeleton holds nothing to release.
    }

    #endregion // IDisposable
}