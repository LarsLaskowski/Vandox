namespace Vandox.Core.Wire;

/// <summary>
/// Computes the CRC-32 checksum (IEEE 802.3, as used by gzip trailers).
/// </summary>
internal static class Crc32
{
    #region Constants

    /// <summary>
    /// The reflected polynomial of the IEEE 802.3 checksum.
    /// </summary>
    private const uint Polynomial = 0xEDB88320u;

    #endregion // Constants

    #region Fields

    /// <summary>
    /// The checksum of every byte value, one table step per input byte.
    /// </summary>
    private static readonly uint[] _table = CreateTable();

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Continues a CRC-32 over further data.
    /// </summary>
    /// <param name="crc">The checksum of the data so far, 0 at the start</param>
    /// <param name="data">The further data</param>
    /// <returns>The checksum including <paramref name="data"/></returns>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        var state = ~crc;

        foreach (var value in data)
        {
            state = _table[(state ^ value) & 0xff] ^ (state >> 8);
        }

        return ~state;
    }

    /// <summary>
    /// Builds the lookup table.
    /// </summary>
    /// <returns>The table</returns>
    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < table.Length; index++)
        {
            var entry = index;

            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) != 0 ? (entry >> 1) ^ Polynomial : entry >> 1;
            }

            table[index] = entry;
        }

        return table;
    }

    #endregion // Methods
}