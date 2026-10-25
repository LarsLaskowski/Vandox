using System.Collections.Concurrent;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Vandox.Backend.Hosting;
using Vandox.Core.Model;
using Vandox.Core.Wire;
using Vandox.Storage;

namespace Vandox.Backend.Ingest;

/// <summary>
/// Answers <c>POST /v1/batches</c> on the ingest listener: checks the token and the agent, decodes the batch, writes it and answers
/// 204 only after the commit.
/// </summary>
internal sealed class IngestEndpoint : IDisposable
{
    #region Constants

    /// <summary>
    /// The header that marks an answer as the refusal of a batch.
    /// </summary>
    private const string RefusedHeader = "Vandox-Refused";

    /// <summary>
    /// The value of the refusal header.
    /// </summary>
    private const string RefusedValue = "batch";

    /// <summary>
    /// The challenge of a refused token.
    /// </summary>
    private const string Challenge = "Bearer";

    /// <summary>
    /// The content type of a batch.
    /// </summary>
    private const string GzipType = "application/gzip";

    /// <summary>
    /// The peer of a request whose address is unknown.
    /// </summary>
    private const string UnknownPeer = "unknown";

    /// <summary>
    /// The reason of a body over the limit.
    /// </summary>
    private const string BodyTooLarge = "body too large";

    /// <summary>
    /// The reason of a batch the store refuses.
    /// </summary>
    private const string RefusedByStore = "refused by the store";

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The time a write may run after the stop signal.
    /// </summary>
    private static readonly TimeSpan _shutdownDeadline = TimeSpan.FromSeconds(10);

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
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Counts the writes in flight and refuses a write once the stop signal fired.
    /// </summary>
    private readonly IngestWriteGate _gate = new();

    /// <summary>
    /// The slots: at most this many requests hold a body buffer and write at once.
    /// </summary>
    private readonly SemaphoreSlim _slots = new(IngestLimits.Slots, IngestLimits.Slots);

    /// <summary>
    /// The body buffers of the slots that are not in use; a slot allocates its buffer on first use and returns it.
    /// </summary>
    private readonly ConcurrentStack<byte[]> _buffers = new();

    /// <summary>
    /// Limits how often the refusal of a token is logged.
    /// </summary>
    private readonly IngestRefusalLog _tokenLog;

    /// <summary>
    /// Limits how often the refusal of an agent is logged.
    /// </summary>
    private readonly IngestRefusalLog _agentLog;

    /// <summary>
    /// Cancels the writes ten seconds after the stop signal; no request cancels them.
    /// </summary>
    private readonly CancellationTokenSource _shutdown;

    /// <summary>
    /// The registration of the stop callback.
    /// </summary>
    private readonly CancellationTokenRegistration _stopping;

    /// <summary>
    /// 1 once the deadline of the writes was started.
    /// </summary>
    private int _deadlineStarted;

    /// <summary>
    /// 1 while ingest is paused for lack of free space.
    /// </summary>
    private int _paused;

    /// <summary>
    /// 1 once the first stored batch was logged.
    /// </summary>
    private int _firstStored;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestEndpoint"/> class. The endpoint registers its stop callback here: from the stop signal on no
    /// write starts, and the writes in flight get ten seconds.
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
        _logger = loggerFactory.CreateLogger("ingest");
        _tokenLog = new IngestRefusalLog(clock);
        _agentLog = new IngestRefusalLog(clock);
        _shutdown = new CancellationTokenSource(Timeout.InfiniteTimeSpan, clock);
        _stopping = lifetime.ApplicationStopping.Register(OnStopping);
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Answers one ingest request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the answer is set</returns>
    internal async Task HandleAsync(HttpContext context)
    {
        try
        {
            await ServeAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The connection is gone: there is nobody to answer.
        }
        catch (Exception exception)
        {
            _logger.IngestRequestFailed(TypeName(exception));
            AnswerUnlessStarted(context, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Closes the write gate and waits for the writes in flight, at most until the shutdown token is cancelled (10 seconds after the stop
    /// signal) and the cancelled writes have rolled back.
    /// </summary>
    /// <returns>A task that completes when no write is in flight</returns>
    internal async Task DrainAsync()
    {
        StartDeadline();

        try
        {
            await _gate.CloseAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _gate.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the name of the type of an exception for a log line; a cancellation is always named <c>OperationCanceledException</c>.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns>The type name</returns>
    private static string TypeName(Exception exception)
    {
        return exception is OperationCanceledException ? nameof(OperationCanceledException) : exception.GetType().Name;
    }

    /// <summary>
    /// Gets the address of the sender as the service sees it.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>The address as text</returns>
    private static string Peer(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? UnknownPeer;
    }

    /// <summary>
    /// Sets the status of the answer; a refusal carries the refusal header. The answer never has a body.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="status">The status</param>
    private static void Answer(HttpContext context, int status)
    {
        context.Response.StatusCode = status;

        if (IngestStatus.IsRefusal(status))
        {
            context.Response.Headers[RefusedHeader] = RefusedValue;
        }
    }

    /// <summary>
    /// Sets the status of the answer when the answer did not start.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="status">The status</param>
    private static void AnswerUnlessStarted(HttpContext context, int status)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        Answer(context, status);
    }

    /// <summary>
    /// Answers 503 to a request whose body could not be read, unless the connection is gone: then there is nobody to answer.
    /// </summary>
    /// <param name="context">The request</param>
    private static void AnswerUnlessAborted(HttpContext context)
    {
        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        Answer(context, StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Limits the body that Kestrel reads to the limit of the endpoint, as a backstop to the check of the endpoint.
    /// </summary>
    /// <param name="context">The request</param>
    private static void LimitBody(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = IngestLimits.MaxBodyBytes;
        }
    }

    /// <summary>
    /// Reads the records of the batch to the end.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="decoder">The decoder, with its header read</param>
    /// <returns>The batch</returns>
    private static async Task<RecordBatch> ReadBatchAsync(IngestRequest request, BatchDecoder decoder)
    {
        var records = new List<DataRecord>();

        while (await decoder.NextAsync(request.Token).ConfigureAwait(false) is { } record)
        {
            records.Add(record);
        }

        return new RecordBatch
               {
                   AgentId = decoder.Header.AgentId,
                   BootId = decoder.Header.BootId,
                   ClockOffsetNs = decoder.Header.ClockOffsetNs,
                   ReceivedAt = request.Received,
                   Records = records
               };
    }

    /// <summary>
    /// Handles the stop signal: no write starts from now on, and the writes in flight get ten seconds.
    /// </summary>
    private void OnStopping()
    {
        _gate.Close();
        StartDeadline();
    }

    /// <summary>
    /// Starts the ten seconds after which the writes in flight are cancelled, once.
    /// </summary>
    private void StartDeadline()
    {
        if (Interlocked.Exchange(ref _deadlineStarted, 1) == 0)
        {
            _shutdown.CancelAfter(_shutdownDeadline);
        }
    }

    /// <summary>
    /// Runs the steps of the request in their order.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns>A task that completes when the answer is set</returns>
    private async Task ServeAsync(HttpContext context)
    {
        var received = _clock.GetUtcNow();

        if (_settings is null)
        {
            Answer(context, StatusCodes.Status503ServiceUnavailable);

            return;
        }

        if (RefusesToken(context, _settings) || RefusesFormat(context) || RefusesLength(context))
        {
            return;
        }

        var left = IngestLimits.RequestBudget - (_clock.GetUtcNow() - received);

        using var budget = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.Zero, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, context.RequestAborted, _lifetime.ApplicationStopping);

        var request = new IngestRequest(context, received, linked.Token);

        if (await WaitForSlotAsync(request).ConfigureAwait(false))
        {
            await ServeInSlotAsync(request, _settings).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks the token of the request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="settings">The settings</param>
    /// <returns><c>true</c> when the token is refused and the request is answered</returns>
    private bool RefusesToken(HttpContext context, IngestSettings settings)
    {
        var authorization = context.Request.Headers.Authorization;

        if (settings.Token.Matches(authorization))
        {
            return false;
        }

        if (_tokenLog.TryTake(out var suppressed))
        {
            _logger.IngestTokenRefused(Peer(context), StringValues.IsNullOrEmpty(authorization) ? "missing" : "wrong", suppressed);
        }

        context.Response.Headers.WWWAuthenticate = Challenge;
        Answer(context, StatusCodes.Status401Unauthorized);

        return true;
    }

    /// <summary>
    /// Checks the content encoding and the content type of the request.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns><c>true</c> when the request is not accepted and is answered</returns>
    private bool RefusesFormat(HttpContext context)
    {
        var headers = context.Request.Headers;

        if (headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            return NotAccepted(context, "content encoding");
        }

        var contentType = headers.ContentType;

        if (contentType.Count == 1 && string.Equals(contentType[0], GzipType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return NotAccepted(context, "content type");
    }

    /// <summary>
    /// Answers 415 and logs the reason.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="reason">The fixed reason</param>
    /// <returns><c>true</c></returns>
    private bool NotAccepted(HttpContext context, string reason)
    {
        Answer(context, StatusCodes.Status415UnsupportedMediaType);
        _logger.IngestRequestNotAccepted(StatusCodes.Status415UnsupportedMediaType, reason);

        return true;
    }

    /// <summary>
    /// Checks the announced length of the body.
    /// </summary>
    /// <param name="context">The request</param>
    /// <returns><c>true</c> when the body is announced over the limit and the request is answered</returns>
    private bool RefusesLength(HttpContext context)
    {
        if (context.Request.ContentLength is > IngestLimits.MaxBodyBytes)
        {
            RefuseTooLarge(context);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Answers 413 as the refusal of a body over the limit.
    /// </summary>
    /// <param name="context">The request</param>
    private void RefuseTooLarge(HttpContext context)
    {
        Answer(context, StatusCodes.Status413PayloadTooLarge);
        _logger.IngestBatchRefused(null, StatusCodes.Status413PayloadTooLarge, BodyTooLarge, null, null);
    }

    /// <summary>
    /// Waits for one of the slots.
    /// </summary>
    /// <param name="request">The request</param>
    /// <returns><c>true</c> when the request holds a slot; <c>false</c> when it gave up and is answered</returns>
    private async Task<bool> WaitForSlotAsync(IngestRequest request)
    {
        try
        {
            await _slots.WaitAsync(request.Token).ConfigureAwait(false);

            return true;
        }
        catch (OperationCanceledException)
        {
            GiveUp(request, StatusCodes.Status503ServiceUnavailable);

            return false;
        }
    }

    /// <summary>
    /// Answers a request whose waiting, reading or decoding was cancelled: nothing when the connection is gone, 503 at the stop signal, otherwise the
    /// time budget is spent.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="timeoutStatus">The status for a spent budget</param>
    private void GiveUp(IngestRequest request, int timeoutStatus)
    {
        var context = request.Context;

        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        if (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            Answer(context, StatusCodes.Status503ServiceUnavailable);

            return;
        }

        TimedOut(context, timeoutStatus);
    }

    /// <summary>
    /// Answers a request that took too long and logs it.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="status">The status</param>
    private void TimedOut(HttpContext context, int status)
    {
        Answer(context, status);
        _logger.IngestRequestTimedOut(Peer(context));
    }

    /// <summary>
    /// Works on a request that holds a slot, and gives the slot and its buffer back at the end.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="settings">The settings</param>
    /// <returns>A task that completes when the answer is set</returns>
    private async Task ServeInSlotAsync(IngestRequest request, IngestSettings settings)
    {
        var buffer = _buffers.TryPop(out var owned) ? owned : new byte[IngestLimits.MaxBodyBytes + 1];

        try
        {
            var length = await ReadBodyAsync(request, buffer).ConfigureAwait(false);

            if (length < 0)
            {
                return;
            }

            var batch = await DecodeAsync(request, buffer, length, settings).ConfigureAwait(false);

            if (batch is not null && HasFreeSpace(request, settings))
            {
                await WriteAsync(request, batch).ConfigureAwait(false);
            }
        }
        finally
        {
            _buffers.Push(buffer);
            _slots.Release();
        }
    }

    /// <summary>
    /// Reads the body into the buffer of the slot, up to one byte over the limit.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="buffer">The buffer of the slot</param>
    /// <returns>The number of bytes; -1 when the request is answered or gone</returns>
    private async Task<int> ReadBodyAsync(IngestRequest request, byte[] buffer)
    {
        var context = request.Context;
        var length = 0;

        LimitBody(context);

        try
        {
            int read;

            do
            {
                read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), request.Token).ConfigureAwait(false);
                length += read;
            }
            while (read > 0 && length <= IngestLimits.MaxBodyBytes);
        }
        catch (OperationCanceledException)
        {
            GiveUp(request, StatusCodes.Status408RequestTimeout);

            return -1;
        }
        catch (BadHttpRequestException exception)
        {
            AnswerBadRequest(context, exception.StatusCode);

            return -1;
        }
        catch (IOException)
        {
            AnswerUnlessAborted(context);

            return -1;
        }

        if (length <= IngestLimits.MaxBodyBytes)
        {
            return length;
        }

        RefuseTooLarge(context);

        return -1;
    }

    /// <summary>
    /// Answers a request that the server itself found unreadable while the body was read.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="status">The status of the server's own error</param>
    private void AnswerBadRequest(HttpContext context, int status)
    {
        if (status == StatusCodes.Status408RequestTimeout)
        {
            TimedOut(context, status);
        }
        else if (status == StatusCodes.Status413PayloadTooLarge)
        {
            RefuseTooLarge(context);
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    /// <summary>
    /// Decodes the batch to its end and checks the agent.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="buffer">The buffer of the slot</param>
    /// <param name="length">The number of bytes of the body</param>
    /// <param name="settings">The settings</param>
    /// <returns>The batch; <c>null</c> when the request is answered</returns>
    private async Task<RecordBatch?> DecodeAsync(IngestRequest request, byte[] buffer, int length, IngestSettings settings)
    {
        using var input = new MemoryStream(buffer, 0, length, false);

        try
        {
            using var decoder = await BatchDecoder.OpenAsync(input, IngestLimits.Wire(), request.Token).ConfigureAwait(false);

            request.Header = decoder.Header;

            if (string.Equals(decoder.Header.AgentId, settings.AgentId, StringComparison.Ordinal))
            {
                return await ReadBatchAsync(request, decoder).ConfigureAwait(false);
            }

            RefuseAgent(request.Context, decoder.Header.AgentId);

            return null;
        }
        catch (WireException exception)
        {
            AnswerWireError(request, exception);

            return null;
        }
        catch (OperationCanceledException)
        {
            GiveUp(request, StatusCodes.Status503ServiceUnavailable);

            return null;
        }
    }

    /// <summary>
    /// Answers 403 to a batch of another agent.
    /// </summary>
    /// <param name="context">The request</param>
    /// <param name="agentId">The agent ID of the batch, which passed the rule of the wire format</param>
    private void RefuseAgent(HttpContext context, string agentId)
    {
        if (_agentLog.TryTake(out var suppressed))
        {
            _logger.IngestAgentRefused(agentId, Peer(context), suppressed);
        }

        Answer(context, StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Answers an error of the decoder by its kind and logs a fixed reason, the line and the field path with the map keys reduced.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="exception">The error</param>
    private void AnswerWireError(IngestRequest request, WireException exception)
    {
        var status = IngestStatus.ForWireError(exception.Kind);
        var reason = IngestStatus.Reason(exception.Kind);

        Answer(request.Context, status);

        if (status == StatusCodes.Status415UnsupportedMediaType)
        {
            _logger.IngestRequestNotAccepted(status, reason);

            return;
        }

        var field = exception.FieldError is null ? null : IngestFieldPath.Reduce(exception.FieldError.Field);

        _logger.IngestBatchRefused(request.Header?.AgentId, status, reason, exception.Line, string.IsNullOrEmpty(field) ? null : field);
    }

    /// <summary>
    /// Checks the free space of the storage volume against the floor.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="settings">The settings</param>
    /// <returns><c>true</c> when the batch may be written; <c>false</c> when the request is answered 503</returns>
    private bool HasFreeSpace(IngestRequest request, IngestSettings settings)
    {
        if (settings.MinFreeBytes <= 0)
        {
            return true;
        }

        var available = _freeSpace.Available();

        if (available is { } free && free >= settings.MinFreeBytes)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _paused, 1) == 0)
        {
            _logger.IngestPaused(available);
        }

        Answer(request.Context, StatusCodes.Status503ServiceUnavailable);

        return false;
    }

    /// <summary>
    /// Writes the batch inside the write gate.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="batch">The batch</param>
    /// <returns>A task that completes when the answer is set</returns>
    private async Task WriteAsync(IngestRequest request, RecordBatch batch)
    {
        if (_gate.TryEnter())
        {
            try
            {
                await CommitAsync(request, batch).ConfigureAwait(false);
            }
            finally
            {
                _gate.Exit();
            }
        }
        else
        {
            Answer(request.Context, StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    /// Writes the batch under the token that only the shutdown deadline cancels and answers by the outcome.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="batch">The batch</param>
    /// <returns>A task that completes when the answer is set</returns>
    private async Task CommitAsync(IngestRequest request, RecordBatch batch)
    {
        WriteResult result;

        try
        {
            result = await _writer.WriteBatchAsync(batch, _shutdown.Token).ConfigureAwait(false);
        }
        catch (InvalidBatchException)
        {
            Answer(request.Context, StatusCodes.Status422UnprocessableEntity);
            _logger.IngestBatchRefused(batch.AgentId, StatusCodes.Status422UnprocessableEntity, RefusedByStore, null, null);

            return;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            Answer(request.Context, StatusCodes.Status503ServiceUnavailable);
            _logger.IngestStoreFailed(batch.AgentId, TypeName(exception));

            return;
        }

        Acknowledge(request, batch, result);
    }

    /// <summary>
    /// Tells whether an exception of the write is a failure of the store, or the cancellation by the shutdown deadline.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns><c>true</c> for a failure the agent may retry</returns>
    private bool IsStoreFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return _shutdown.IsCancellationRequested;
        }

        return exception is StoreException or SqliteException or IOException;
    }

    /// <summary>
    /// Answers 204 after the commit and logs the stored batch.
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="batch">The batch</param>
    /// <param name="result">The counts of the write</param>
    private void Acknowledge(IngestRequest request, RecordBatch batch, WriteResult result)
    {
        Answer(request.Context, StatusCodes.Status204NoContent);

        if (Interlocked.CompareExchange(ref _paused, 0, 1) == 1)
        {
            _logger.IngestResumed();
        }

        if (Interlocked.Exchange(ref _firstStored, 1) == 0)
        {
            _logger.IngestFirstBatchStored(batch.AgentId);
        }

        _logger.IngestBatchStored(batch.AgentId, request.Header?.Mode ?? string.Empty, batch.Records.Count, result.Stored, result.Duplicates);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        _shutdown.Dispose();
        _slots.Dispose();
    }

    #endregion // IDisposable
}