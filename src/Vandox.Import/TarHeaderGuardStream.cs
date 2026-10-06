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
    /// Examines a complete header block.
    /// </summary>
    /// <exception cref="TarMetadataTooLargeException">An extended header is too large</exception>
    private void Inspect()
    {
        var type = _header[TypeOffset];
        var size = ReadSize(_header);

        if (type is (byte)'x' or (byte)'g' or (byte)'L' or (byte)'K' && size > ImportLimits.MaxTarMetadataBytes)
        {
            throw new TarMetadataTooLargeException();
        }

        if (HasData(type) && size > 0)
        {
            _skip = size > long.MaxValue - BlockSize ? long.MaxValue : ((size + BlockSize - 1) / BlockSize) * BlockSize;
        }
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