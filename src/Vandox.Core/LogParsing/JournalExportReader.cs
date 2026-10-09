using System.Buffers.Binary;

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the entries of a journal export with bounded memory: eight fields are kept, everything else is read into a
/// reused buffer and discarded, and a declared length never sizes an allocation.
/// </summary>
internal sealed class JournalExportReader
{
    #region Constants

    /// <summary>
    /// The longest field name in bytes.
    /// </summary>
    internal const int MaxFieldNameBytes = 64;

    /// <summary>
    /// The skip reason of an entry with an invalid field name.
    /// </summary>
    internal const string MalformedField = "malformed field";

    /// <summary>
    /// The skip reason of an entry that the input cuts off.
    /// </summary>
    internal const string TruncatedEntry = "truncated entry";

    private const int ReadBufferBytes = 16 * 1024;
    private const int MessageKeepBytes = Model.ModelLimits.MaxTextBytes;
    private const int ShortKeepBytes = Model.ModelLimits.MaxShortTextBytes;
    private const int NumberKeepBytes = 32;
    private const int LengthBytes = 8;
    private const int Utf8MaxSequence = 4;

    private const string FieldTimestamp = "__REALTIME_TIMESTAMP";
    private const string FieldHostname = "_HOSTNAME";
    private const string FieldIdentifier = "SYSLOG_IDENTIFIER";
    private const string FieldComm = "_COMM";
    private const string FieldPid = "_PID";
    private const string FieldSyslogPid = "SYSLOG_PID";
    private const string FieldPriority = "PRIORITY";
    private const string FieldMessage = "MESSAGE";

    #endregion // Constants

    #region Fields

    private readonly Stream _input;
    private readonly byte[] _buffer = new byte[ReadBufferBytes];
    private readonly byte[] _name = new byte[MaxFieldNameBytes];
    private readonly byte[] _value = new byte[MessageKeepBytes];
    private readonly byte[] _length = new byte[LengthBytes];
    private int _position;
    private int _count;
    private int _nameLength;
    private int _valueLength;
    private bool _valueCut;
    private bool _ended;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalExportReader"/> class.
    /// </summary>
    /// <param name="input">The content</param>
    internal JournalExportReader(Stream input)
    {
        _input = input;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the next entry. A malformed or cut-off entry is returned with <see cref="JournalEntry.Problem"/> set; after a
    /// cut-off entry the input is considered ended.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The entry, or <c>null</c> after the last one</returns>
    internal async ValueTask<JournalEntry?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_ended)
        {
            return null;
        }

        var entry = new JournalEntry();
        var started = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kind = await ReadFieldStartAsync(cancellationToken).ConfigureAwait(false);

            if (kind == JournalFieldKind.End)
            {
                _ended = true;

                return started ? entry : null;
            }

            if (kind == JournalFieldKind.EmptyLine)
            {
                if (started)
                {
                    return entry;
                }

                continue;
            }

            started = true;

            if (kind is JournalFieldKind.Text or JournalFieldKind.Binary)
            {
                kind = await ReadFieldAsync(entry, kind == JournalFieldKind.Binary, cancellationToken).ConfigureAwait(false);
            }

            if (kind == JournalFieldKind.Malformed)
            {
                entry.Problem = MalformedField;

                return entry;
            }

            if (kind == JournalFieldKind.Cut)
            {
                entry.Problem = TruncatedEntry;
                _ended = true;

                return entry;
            }
        }
    }

    /// <summary>
    /// Returns the length at which a raw cut text ends without a half character: an incomplete but valid UTF-8 sequence at
    /// the end is dropped whole.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <returns>The length to keep</returns>
    private static int WholeLength(ReadOnlySpan<byte> bytes)
    {
        var first = Math.Max(0, bytes.Length - (Utf8MaxSequence - 1));

        for (var index = bytes.Length - 1; index >= first; index--)
        {
            var value = bytes[index];

            if ((value & 0xC0) == 0x80)
            {
                continue;
            }

            var needed = value switch
                         {
                             >= 0xF0 and <= 0xF4 => 4,
                             >= 0xE0 and < 0xF0 => 3,
                             >= 0xC2 and < 0xE0 => 2,
                             _ => 1
                         };

            return index + needed > bytes.Length ? index : bytes.Length;
        }

        return bytes.Length;
    }

    /// <summary>
    /// Tells whether a byte may be part of a field name at a position.
    /// </summary>
    /// <param name="value">The byte</param>
    /// <param name="index">The position in the name</param>
    /// <returns><c>true</c> for <c>A-Z</c>, <c>_</c> and, after the first byte, <c>0-9</c></returns>
    private static bool IsNameByte(byte value, int index)
    {
        return value is >= (byte)'A' and <= (byte)'Z' || value == (byte)'_' || (index > 0 && value is >= (byte)'0' and <= (byte)'9');
    }

    /// <summary>
    /// Decides how many bytes of a short text field are kept.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="name">The field name</param>
    /// <param name="slot">The name of the kept field, or <c>null</c></param>
    /// <returns>The number of bytes to keep</returns>
    private static int ShortFieldKeepBytes(JournalEntry entry, ReadOnlySpan<byte> name, out string? slot)
    {
        slot = null;

        if (name.SequenceEqual("_HOSTNAME"u8))
        {
            slot = FieldHostname;

            return entry.Hostname is null ? ShortKeepBytes : 0;
        }

        if (name.SequenceEqual("SYSLOG_IDENTIFIER"u8))
        {
            slot = FieldIdentifier;

            return entry.SyslogIdentifier is null ? ShortKeepBytes : 0;
        }

        if (name.SequenceEqual("_COMM"u8))
        {
            slot = FieldComm;

            return entry.Comm is null ? ShortKeepBytes : 0;
        }

        return 0;
    }

    /// <summary>
    /// Makes sure unread bytes are in the buffer.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>false</c> at the end of the input</returns>
    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_position < _count)
        {
            return true;
        }

        _position = 0;
        _count = await _input.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

        return _count > 0;
    }

    /// <summary>
    /// Reads the name of the next field and the character that ends it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>What was found; the name is in the name buffer for <see cref="JournalFieldKind.Text"/> and <see cref="JournalFieldKind.Binary"/></returns>
    private async ValueTask<JournalFieldKind> ReadFieldStartAsync(CancellationToken cancellationToken)
    {
        _nameLength = 0;

        while (await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = _buffer[_position];

            if (value == (byte)'\n' || value == (byte)'=')
            {
                _position++;

                return await FinishNameAsync(value == (byte)'\n', cancellationToken).ConfigureAwait(false);
            }

            if (_nameLength < MaxFieldNameBytes && IsNameByte(value, _nameLength))
            {
                _name[_nameLength++] = value;
                _position++;
            }
            else
            {
                return await ResynchronizeAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }

        return _nameLength == 0 ? JournalFieldKind.End : JournalFieldKind.Cut;
    }

    /// <summary>
    /// Handles the end of a field name.
    /// </summary>
    /// <param name="lineFeed">Whether a line feed ended the name; otherwise it was an equals sign</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>What was found</returns>
    private async ValueTask<JournalFieldKind> FinishNameAsync(bool lineFeed, CancellationToken cancellationToken)
    {
        if (_nameLength == 0)
        {
            return lineFeed ? JournalFieldKind.EmptyLine : await ResynchronizeAsync(false, cancellationToken).ConfigureAwait(false);
        }

        return lineFeed ? JournalFieldKind.Binary : JournalFieldKind.Text;
    }

    /// <summary>
    /// Skips input up to and including the next empty line, in the read buffer, so the memory does not depend on the input.
    /// </summary>
    /// <param name="atLineStart">Whether the read position is at the start of a line</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <see cref="JournalFieldKind.Malformed"/></returns>
    private async ValueTask<JournalFieldKind> ResynchronizeAsync(bool atLineStart, CancellationToken cancellationToken)
    {
        while (await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            var span = _buffer.AsSpan(_position, _count - _position);

            if (atLineStart && span[0] == (byte)'\n')
            {
                _position++;

                break;
            }

            var index = span.IndexOf((byte)'\n');

            if (index < 0)
            {
                _position = _count;
                atLineStart = false;
            }
            else
            {
                _position += index + 1;
                atLineStart = true;
            }
        }

        return JournalFieldKind.Malformed;
    }

    /// <summary>
    /// Reads the value of the field whose name is in the name buffer and stores it when the field is kept.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="binary">Whether the field is binary</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <see cref="JournalFieldKind.Text"/> when the field was read, <see cref="JournalFieldKind.Cut"/> when the input ended inside it and <see cref="JournalFieldKind.Malformed"/> when a binary value is not closed by a line feed</returns>
    private async ValueTask<JournalFieldKind> ReadFieldAsync(JournalEntry entry, bool binary, CancellationToken cancellationToken)
    {
        var keep = KeepBytes(entry, out var slot);
        var result = binary
                         ? await ReadBinaryAsync(keep, cancellationToken).ConfigureAwait(false)
                         : await ReadTextAsync(keep, cancellationToken).ConfigureAwait(false);

        if (result == JournalFieldKind.Text && keep > 0)
        {
            Store(entry, slot);
        }

        return result;
    }

    /// <summary>
    /// Decides how many bytes of the current field are kept.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="slot">The name of the kept field, or <c>null</c></param>
    /// <returns>The number of bytes to keep; 0 for a field that is not kept or already set</returns>
    private int KeepBytes(JournalEntry entry, out string? slot)
    {
        var name = _name.AsSpan(0, _nameLength);

        slot = null;

        if (name.SequenceEqual("MESSAGE"u8))
        {
            slot = FieldMessage;

            return entry.Message is null ? MessageKeepBytes : 0;
        }

        if (name.SequenceEqual("__REALTIME_TIMESTAMP"u8))
        {
            slot = FieldTimestamp;

            return entry.Realtime is null ? NumberKeepBytes : 0;
        }

        if (name.SequenceEqual("PRIORITY"u8))
        {
            slot = FieldPriority;

            return entry.Priority is null ? NumberKeepBytes : 0;
        }

        if (name.SequenceEqual("_PID"u8))
        {
            slot = FieldPid;

            return entry.Pid is null ? NumberKeepBytes : 0;
        }

        if (name.SequenceEqual("SYSLOG_PID"u8))
        {
            slot = FieldSyslogPid;

            return entry.SyslogPid is null ? NumberKeepBytes : 0;
        }

        return ShortFieldKeepBytes(entry, name, out slot);
    }

    /// <summary>
    /// Reads a text value up to the line feed, keeping its first bytes.
    /// </summary>
    /// <param name="keep">The number of bytes to keep</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <see cref="JournalFieldKind.Text"/>, or <see cref="JournalFieldKind.Cut"/> when the input ended before the line feed</returns>
    private async ValueTask<JournalFieldKind> ReadTextAsync(int keep, CancellationToken cancellationToken)
    {
        _valueLength = 0;
        _valueCut = false;

        while (await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            var span = _buffer.AsSpan(_position, _count - _position);
            var index = span.IndexOf((byte)'\n');
            var take = index >= 0 ? index : span.Length;

            Append(span[..take], keep);
            _position += take;

            if (index >= 0)
            {
                _position++;

                return JournalFieldKind.Text;
            }
        }

        return JournalFieldKind.Cut;
    }

    /// <summary>
    /// Reads the length and bytes of a binary value, keeping its first bytes.
    /// </summary>
    /// <param name="keep">The number of bytes to keep</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <see cref="JournalFieldKind.Text"/> when the value was read, <see cref="JournalFieldKind.Cut"/> when the input ended or the length is not possible, <see cref="JournalFieldKind.Malformed"/> when the closing line feed is missing</returns>
    private async ValueTask<JournalFieldKind> ReadBinaryAsync(int keep, CancellationToken cancellationToken)
    {
        _valueLength = 0;
        _valueCut = false;

        if (await ReadExactAsync(_length, cancellationToken).ConfigureAwait(false))
        {
            var length = BinaryPrimitives.ReadUInt64LittleEndian(_length);

            if (length < 1UL << 63)
            {
                return await ReadBinaryValueAsync(length, keep, cancellationToken).ConfigureAwait(false);
            }
        }

        return JournalFieldKind.Cut;
    }

    /// <summary>
    /// Reads the bytes of a binary value and its closing line feed, keeping the first bytes.
    /// </summary>
    /// <param name="length">The declared length; it only counts bytes read, it never sizes a buffer</param>
    /// <param name="keep">The number of bytes to keep</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns the result as <see cref="ReadBinaryAsync"/></returns>
    private async ValueTask<JournalFieldKind> ReadBinaryValueAsync(ulong length, int keep, CancellationToken cancellationToken)
    {
        while (length > 0 && await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            var take = (int)Math.Min(length, (ulong)(_count - _position));

            Append(_buffer.AsSpan(_position, take), keep);
            _position += take;
            length -= (ulong)take;
        }

        if (length == 0 && await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return _buffer[_position++] == (byte)'\n' ? JournalFieldKind.Text : await ResynchronizeAsync(false, cancellationToken).ConfigureAwait(false);
        }

        return JournalFieldKind.Cut;
    }

    /// <summary>
    /// Fills a buffer from the input.
    /// </summary>
    /// <param name="target">The buffer</param>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>A task that returns <c>false</c> when the input ended first</returns>
    private async ValueTask<bool> ReadExactAsync(byte[] target, CancellationToken cancellationToken)
    {
        var filled = 0;

        while (filled < target.Length && await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            var take = Math.Min(target.Length - filled, _count - _position);

            _buffer.AsSpan(_position, take).CopyTo(target.AsSpan(filled));
            filled += take;
            _position += take;
        }

        return filled == target.Length;
    }

    /// <summary>
    /// Appends bytes of a value to the kept bytes, up to the number to keep.
    /// </summary>
    /// <param name="bytes">The bytes</param>
    /// <param name="keep">The number of bytes to keep</param>
    private void Append(ReadOnlySpan<byte> bytes, int keep)
    {
        var room = keep - _valueLength;
        var copy = Math.Min(room, bytes.Length);

        if (copy > 0)
        {
            bytes[..copy].CopyTo(_value.AsSpan(_valueLength));
            _valueLength += copy;
        }

        if (copy < bytes.Length)
        {
            _valueCut = true;
        }
    }

    /// <summary>
    /// Decodes the kept bytes into the field of the entry.
    /// </summary>
    /// <param name="entry">The entry</param>
    /// <param name="slot">The name of the field</param>
    private void Store(JournalEntry entry, string? slot)
    {
        var bytes = _value.AsSpan(0, _valueLength);
        var rawCut = _valueCut;

        if (rawCut)
        {
            bytes = bytes[..WholeLength(bytes)];
        }

        var limit = slot switch
                    {
                        FieldMessage => MessageKeepBytes,
                        FieldHostname or FieldIdentifier or FieldComm => ShortKeepBytes,
                        _ => NumberKeepBytes
                    };
        var text = Utf8Text.Decode(bytes, limit, out var cut);
        var isText = slot is FieldMessage or FieldHostname or FieldIdentifier or FieldComm;

        if (isText && (cut || rawCut))
        {
            entry.Truncated = true;
        }

        switch (slot)
        {
            case FieldMessage:
                entry.Message = text;
                break;

            case FieldTimestamp:
                entry.Realtime = text;
                break;

            case FieldHostname:
                entry.Hostname = text;
                break;

            case FieldIdentifier:
                entry.SyslogIdentifier = text;
                break;

            case FieldComm:
                entry.Comm = text;
                break;

            case FieldPid:
                entry.Pid = text;
                break;

            case FieldSyslogPid:
                entry.SyslogPid = text;
                break;

            default:
                entry.Priority = text;
                break;
        }
    }

    #endregion // Methods
}