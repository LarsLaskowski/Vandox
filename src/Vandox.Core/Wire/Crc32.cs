namespace Vandox.Core.Wire;

#pragma warning disable RH2003, S2325
/// <summary>
/// Computes the CRC-32 checksum (IEEE 802.3, as used by gzip trailers).
/// </summary>
internal static class Crc32
{
    #region Methods

    /// <summary>
    /// Continues a CRC-32 over further data.
    /// </summary>
    /// <param name="crc">The checksum of the data so far, 0 at the start</param>
    /// <param name="data">The further data</param>
    /// <returns>The checksum including <paramref name="data"/></returns>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}