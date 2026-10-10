using System.IO.Compression;
using System.Text.Json;

using Vandox.Core.IO;
using Vandox.Core.Model;

namespace Vandox.Core.Wire;

/// <summary>
/// Reads a batch record by record from a gzip-compressed stream of JSON lines. The first line is the header, every
/// further line one record. The decoder allocates only within <see cref="WireLimits"/>.
/// </summary>
public sealed class BatchDecoder : IDisposable
{
    #region Fields

    private readonly GZipStream _gzip;
    private readonly WireLineReader _lines;
    private readonly WireLimits _limits;
    private int _line;
    private int _count;
    private ulong _last;
    private bool _finished;
    private WireException? _failure;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="BatchDecoder"/> class.
    /// </summary>
    /// <param name="gzip">The decompressing stream</param>
    /// <param name="limits">The limits, with defaults filled in</param>
    private BatchDecoder(GZipStream gzip, WireLimits limits)
    {
        _gzip = gzip;
        _limits = limits;
        _lines = new WireLineReader(gzip, limits.MaxLineBytes, limits.MaxBatchBytes);
        Header = new WireHeader();
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the validated header.
    /// </summary>
    public WireHeader Header { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Opens the batch in <paramref name="input"/> and reads and validates its header. The decoder does not close
    /// <paramref name="input"/>.
    /// </summary>
    /// <param name="input">The compressed stream</param>
    /// <param name="limits">The limits; unset values select the defaults</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The decoder</returns>
    /// <exception cref="WireException">The header is missing or invalid</exception>
    public static async Task<BatchDecoder> OpenAsync(Stream input, WireLimits? limits, CancellationToken cancellationToken)
    {
        StrictGzip.Require();

        var defaults = new WireLimits();
        var effective = new WireLimits
                        {
                            MaxLineBytes = limits is { MaxLineBytes: > 0 } ? Math.Min(limits.MaxLineBytes, int.MaxValue - 1) : defaults.MaxLineBytes,
                            MaxBatchBytes = limits is { MaxBatchBytes: > 0 } ? limits.MaxBatchBytes : defaults.MaxBatchBytes,
                            MaxRecords = limits is { MaxRecords: > 0 } ? limits.MaxRecords : defaults.MaxRecords
                        };
        var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        var decoder = new BatchDecoder(gzip, effective);

        try
        {
            await decoder.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            decoder.Dispose();

            throw;
        }

        return decoder;
    }

    /// <summary>
    /// Reads the next record.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The record, or <c>null</c> after the last one</returns>
    /// <exception cref="WireException">The stream is malformed or breaks a limit or a rule; every further call throws the same error</exception>
    public async Task<DataRecord?> NextAsync(CancellationToken cancellationToken)
    {
        if (_failure is not null)
        {
            throw _failure;
        }

        if (_finished)
        {
            return null;
        }

        ReadOnlyMemory<byte>? raw;

        try
        {
            raw = await _lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WireException exception)
        {
            throw Fail(exception.Kind, exception.Reason, null, _line + 1);
        }

        _line++;

        if (raw is null)
        {
            if (_count == 0)
            {
                throw Fail(WireErrorKind.EmptyBatch, "batch has no records", null, _line);
            }

            _finished = true;

            return null;
        }

        if (_count >= _limits.MaxRecords)
        {
            throw Fail(WireErrorKind.LimitExceeded, $"more than {_limits.MaxRecords} records", null, _line);
        }

        var record = DecodeRecord(raw.Value.Span);

        if (record.Seq <= _last)
        {
            throw Fail(WireErrorKind.Sequence, $"seq {record.Seq} after {_last}", null, _line);
        }

        _last = record.Seq;
        _count++;

        return record;
    }

    /// <summary>
    /// Reads <c>format_major</c> from the header.
    /// </summary>
    /// <param name="root">The header document</param>
    /// <returns>The major version, <see cref="int.MinValue"/> when it is no 32-bit integer, or <c>null</c> when it is missing</returns>
    private static int? ReadMajor(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format_major", out var major) && major.ValueKind == JsonValueKind.Number)
        {
            return major.TryGetInt32(out var value) ? value : int.MinValue;
        }

        return null;
    }

    /// <summary>
    /// Reads the first line and checks the version and the header.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that completes when the header is read</returns>
    private async Task ReadHeaderAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? raw;

        try
        {
            raw = await _lines.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WireException exception)
        {
            throw Fail(exception.Kind, exception.Reason, null, 1);
        }

        _line = 1;

        if (raw is null)
        {
            throw Fail(WireErrorKind.Malformed, "no header line", null, 1);
        }

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(raw.Value);

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Fail(WireErrorKind.Malformed, "header is not valid JSON", null, 1);
        }

        var major = ReadMajor(root) ?? throw Fail(WireErrorKind.UnsupportedVersion, "format_major missing", null, 1);

        if (major != WireFormat.MajorVersion)
        {
            throw Fail(WireErrorKind.UnsupportedVersion, "major version is not supported", null, 1);
        }

        WireHeader? header;

        try
        {
            header = root.Deserialize<WireHeader>(PayloadRegistry.Options);
        }
        catch (JsonException)
        {
            throw Fail(WireErrorKind.Malformed, "header does not fit the format", null, 1);
        }

        var error = header?.Validate();

        if (header is null || error is not null)
        {
            throw Fail(WireErrorKind.Invalid, "invalid header", error, 1);
        }

        Header = header;
        _failure = null;
    }

    /// <summary>
    /// Decodes and validates one record line.
    /// </summary>
    /// <param name="line">The JSON text of the line</param>
    /// <returns>The record</returns>
    private DataRecord DecodeRecord(ReadOnlySpan<byte> line)
    {
        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(line.ToArray());

            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Fail(WireErrorKind.Malformed, "record is not valid JSON", null, _line);
        }

        if (root.ValueKind != JsonValueKind.Object && root.ValueKind != JsonValueKind.Null)
        {
            throw Fail(WireErrorKind.Malformed, "record is not a JSON object", null, _line);
        }

        Envelope envelope;

        try
        {
            envelope = root.Deserialize<Envelope>(PayloadRegistry.Options) ?? new Envelope();
        }
        catch (JsonException)
        {
            throw Fail(WireErrorKind.Malformed, "record does not fit the format", null, _line);
        }

        if (PayloadRegistry.TypeOf(envelope.Kind) is null)
        {
            throw Fail(WireErrorKind.UnknownKind, $"unknown record kind {FieldError.QuoteName(envelope.Kind)}", null, _line);
        }

        if (envelope.Data is null || envelope.Data.Value.ValueKind == JsonValueKind.Null)
        {
            throw Fail(WireErrorKind.Invalid, "invalid record", new FieldError("data", "required"), _line);
        }

        IPayload? payload;

        try
        {
            payload = PayloadRegistry.Deserialize(envelope.Kind, envelope.Data.Value);
        }
        catch (JsonException)
        {
            throw Fail(WireErrorKind.Malformed, "payload does not fit its kind", null, _line);
        }

        var record = new DataRecord
                     {
                         Origin = RecordOrigin.Agent,
                         Source = envelope.Source,
                         Seq = envelope.Seq,
                         CapturedAt = envelope.CapturedAt,
                         Data = payload
                     };
        var error = record.Validate();

        if (error is not null)
        {
            throw Fail(WireErrorKind.Invalid, "invalid record", error, _line);
        }

        return record;
    }

    /// <summary>
    /// Makes an error sticky: the decoder reports it again on every further call.
    /// </summary>
    /// <param name="kind">The class of the error</param>
    /// <param name="reason">What is wrong</param>
    /// <param name="fieldError">The broken rule of the model, if any</param>
    /// <param name="line">The line of the stream</param>
    /// <returns>The error to throw</returns>
    private WireException Fail(WireErrorKind kind, string reason, FieldError? fieldError, int line)
    {
        _failure = new WireException(kind, line, reason, fieldError);

        return _failure;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _gzip.Dispose();
    }

    #endregion // IDisposable
}