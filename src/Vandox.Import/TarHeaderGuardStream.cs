using System.Globalization;

namespace Vandox.Import;

/// <summary>
/// A forward-only view of a tar stream that follows the 512-byte block structure and refuses an extended header
/// (PAX <c>x</c> and <c>g</c>, GNU long name <c>L</c> and long link <c>K</c>) that declares more than
/// <see cref="ImportLimits.MaxTarMetadataBytes"/>. The tar reader allocates the size a header declares, so without
/// the check a few kilobytes of input could ask for gigabytes of memory.
/// </summary>
internal sealed class TarHeaderGuardStream : FilterStream
{
    #region Constants

    private const int BlockSize = 512;
    private const int TypeOffset = 156;
    private const int SizeOffset = 124;
    private const int SizeLength = 12;

    #endregion // Constants

    #region Fields

    private readonly byte[] _header = new byte[BlockSize];
    private int _filled;
    private long _skip;
    private byte[]? _collected;
    private int _collectedFilled;
    private long _afterCollect;
    private bool _collectsGlobal;
    private long? _entrySize;
    private long? _globalSize;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TarHeaderGuardStream"/> class.
    /// </summary>
    /// <param name="inner">The tar stream</param>
    internal TarHeaderGuardStream(Stream inner)
        : base(inner)
    {
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the size field of a header: octal digits, or base-256 when the high bit of the first byte is set.
    /// </summary>
    /// <param name="header">The header block</param>
    /// <returns>The size; <see cref="long.MaxValue"/> when it does not fit, 0 when the field is not a number</returns>
    private static long ReadSize(ReadOnlySpan<byte> header)
    {
        var field = header.Slice(SizeOffset, SizeLength);

        if ((field[0] & 0x80) != 0)
        {
            long value = field[0] & 0x7F;

            for (var index = 1; index < field.Length; index++)
            {
                if (value > (long.MaxValue >> 8) - 1)
                {
                    return long.MaxValue;
                }

                value = (value << 8) | field[index];
            }

            return value;
        }

        var text = System.Text.Encoding.ASCII.GetString(field).Trim(' ', '\0');

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _) && text.All(character => character is >= '0' and <= '7')
                   ? Convert.ToInt64(text, 8)
                   : 0;
    }

    /// <summary>
    /// Tells whether an entry type has a data section the reader skips: link and special types carry none.
    /// </summary>
    /// <param name="type">The type flag</param>
    /// <returns><c>true</c> when the size field counts</returns>
    private static bool HasData(byte type)
    {
        return type is not ((byte)'1' or (byte)'2' or (byte)'3' or (byte)'4' or (byte)'5' or (byte)'6');
    }

    /// <summary>
    /// Reads the <c>size</c> record of the data of a PAX extended header (<c>length key=value\n</c> records).
    /// </summary>
    /// <param name="data">The data of the extended header</param>
    /// <returns>The size; <c>null</c> when there is no valid record</returns>
    private static long? ReadPaxSize(ReadOnlySpan<byte> data)
    {
        long? size = null;
        var position = 0;

        while (position < data.Length)
        {
            var space = data[position..].IndexOf((byte)' ');
            var length = 0L;

            if (space <= 0 || space > 10 || ParseNumber(data.Slice(position, space), out length) is null)
            {
                break;
            }

            if (length <= space + 1 || position + length > data.Length)
            {
                break;
            }

            var record = System.Text.Encoding.UTF8.GetString(data.Slice(position + space + 1, (int)length - space - 1)).TrimEnd('\n');

            if (record.StartsWith("size=", StringComparison.Ordinal))
            {
                size = long.TryParse(record.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
            }

            position += (int)length;
        }

        return size;
    }

    /// <summary>
    /// Parses ASCII decimal digits.
    /// </summary>
    /// <param name="text">The digits</param>
    /// <param name="value">The number</param>
    /// <returns>The number, or <c>null</c> when the text is not a number</returns>
    private static long? ParseNumber(ReadOnlySpan<byte> text, out long value)
    {
        var parsed = long.TryParse(System.Text.Encoding.ASCII.GetString(text), NumberStyles.None, CultureInfo.InvariantCulture, out value);

        return parsed ? value : null;
    }

    /// <summary>
    /// Returns the number of bytes a data section of <paramref name="size"/> bytes occupies, padded to whole blocks.
    /// </summary>
    /// <param name="size">The size</param>
    /// <returns>The padded size</returns>
    private static long Padded(long size)
    {
        return size > long.MaxValue - BlockSize ? long.MaxValue : ((size + BlockSize - 1) / BlockSize) * BlockSize;
    }

    /// <summary>
    /// Examines a complete header block.
    /// </summary>
    /// <exception cref="TarMetadataTooLargeException">An extended header is too large</exception>
    private void Inspect()
    {
        var type = _header[TypeOffset];
        var size = ReadSize(_header);

        if (type is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K')
        {
            if (size > ImportLimits.MaxTarMetadataBytes)
            {
                throw new TarMetadataTooLargeException();
            }

            if (size > 0)
            {
                if (type is (byte)'x' or (byte)'g')
                {
                    _collected = new byte[size];
                    _collectedFilled = 0;
                    _collectsGlobal = type == (byte)'g';
                    _afterCollect = Padded(size) - size;
                }
                else
                {
                    _skip = Padded(size);
                }
            }

            return;
        }

        // A size record of a preceding PAX header replaces the size field, as it does for the tar reader.
        var effective = _entrySize ?? _globalSize ?? size;

        _entrySize = null;

        if (HasData(type) && effective > 0)
        {
            _skip = Padded(effective);
        }
    }

    /// <summary>
    /// Takes the size record of a collected PAX header.
    /// </summary>
    private void FinishCollecting()
    {
        var size = ReadPaxSize(_collected ?? []);

        if (_collectsGlobal)
        {
            _globalSize = size ?? _globalSize;
        }
        else
        {
            _entrySize = size ?? _entrySize;
        }

        _collected = null;
        _skip = _afterCollect;
    }

    #endregion // Methods

    #region FilterStream

    /// <inheritdoc />
    protected override void Observe(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_skip > 0)
            {
                var skipped = (int)Math.Min(_skip, data.Length);

                _skip -= skipped;
                data = data[skipped..];

                continue;
            }

            if (_collected is not null)
            {
                var copy = Math.Min(_collected.Length - _collectedFilled, data.Length);

                data[..copy].CopyTo(_collected.AsSpan(_collectedFilled));
                _collectedFilled += copy;
                data = data[copy..];

                if (_collectedFilled == _collected.Length)
                {
                    FinishCollecting();
                }

                continue;
            }

            var take = Math.Min(BlockSize - _filled, data.Length);

            data[..take].CopyTo(_header.AsSpan(_filled));
            _filled += take;
            data = data[take..];

            if (_filled == BlockSize)
            {
                _filled = 0;
                Inspect();
            }
        }
    }

    #endregion // FilterStream
}