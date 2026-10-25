using System.IO.Compression;

namespace Vandox.Core.Tests;

/// <summary>
/// Writes gzip members by hand, with the optional header fields and bytes after the last member that <see cref="GZipStream"/> never writes.
/// </summary>
internal static class GzipFixture
{
    #region Constants

    /// <summary>
    /// The flag of the header checksum.
    /// </summary>
    private const byte FlagHeaderCrc = 0x02;

    /// <summary>
    /// The flag of the extra field.
    /// </summary>
    private const byte FlagExtra = 0x04;

    /// <summary>
    /// The flag of the file name.
    /// </summary>
    private const byte FlagName = 0x08;

    /// <summary>
    /// The flag of the comment.
    /// </summary>
    private const byte FlagComment = 0x10;

    /// <summary>
    /// The length of the header that <see cref="GZipStream"/> writes.
    /// </summary>
    private const int PlainHeaderLength = 10;

    /// <summary>
    /// The length of the trailer: CRC-32 and size.
    /// </summary>
    private const int TrailerLength = 8;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Writes one member.
    /// </summary>
    /// <param name="plain">The content</param>
    /// <param name="nameBytes">The length of the file name, 0 for none</param>
    /// <param name="commentBytes">The length of the comment, 0 for none</param>
    /// <param name="extraBytes">The length of the extra field, 0 for none</param>
    /// <param name="headerCrc"><c>"right"</c> or <c>"wrong"</c> for a header checksum of that kind, <c>null</c> for none</param>
    /// <returns>The bytes of the member</returns>
    internal static byte[] Member(byte[] plain, int nameBytes = 0, int commentBytes = 0, int extraBytes = 0, string? headerCrc = null)
    {
        var whole = Deflated(plain);
        var header = new List<byte>
                     {
                         0x1f,
                         0x8b,
                         0x08,
                         0,
                         0,
                         0,
                         0,
                         0,
                         0,
                         0xff
                     };
        byte flags = 0;

        if (extraBytes > 0)
        {
            flags |= FlagExtra;
            header.Add((byte)extraBytes);
            header.Add((byte)(extraBytes >> 8));
            header.AddRange(Enumerable.Repeat((byte)'x', extraBytes));
        }

        if (nameBytes > 0)
        {
            flags |= FlagName;
            header.AddRange(Enumerable.Repeat((byte)'n', nameBytes));
            header.Add(0);
        }

        if (commentBytes > 0)
        {
            flags |= FlagComment;
            header.AddRange(Enumerable.Repeat((byte)'c', commentBytes));
            header.Add(0);
        }

        if (headerCrc is not null)
        {
            flags |= FlagHeaderCrc;
        }

        header[3] = flags;

        if (headerCrc is not null)
        {
            var sum = (ushort)Reference(header.ToArray());

            if (headerCrc == "wrong")
            {
                sum ^= 0xffff;
            }

            header.Add((byte)sum);
            header.Add((byte)(sum >> 8));
        }

        return [.. header, .. whole.AsSpan(PlainHeaderLength)];
    }

    /// <summary>
    /// Writes the lines as members of the gzip framing a shared decoder case describes.
    /// </summary>
    /// <param name="lines">The lines, each followed by a line feed</param>
    /// <param name="members">The number of members the lines are split into</param>
    /// <param name="trailing">The bytes after the last member</param>
    /// <param name="nameBytes">The length of the file name of each member</param>
    /// <param name="commentBytes">The length of the comment of each member</param>
    /// <param name="extraBytes">The length of the extra field of each member</param>
    /// <param name="headerCrc"><c>"right"</c> or <c>"wrong"</c> for a header checksum of that kind, <c>null</c> for none</param>
    /// <returns>The stream, positioned at its start</returns>
    internal static MemoryStream Frame(string[] lines, int members, byte[] trailing, int nameBytes, int commentBytes, int extraBytes, string? headerCrc)
    {
        var count = Math.Max(members, 1);
        var output = new MemoryStream();

        for (var index = 0; index < count; index++)
        {
            var part = lines.Skip(index * lines.Length / count).Take(((index + 1) * lines.Length / count) - (index * lines.Length / count));
            var plain = System.Text.Encoding.UTF8.GetBytes(string.Concat(part.Select(line => line + "\n")));

            output.Write(Member(plain, nameBytes, commentBytes, extraBytes, headerCrc));
        }

        output.Write(trailing);
        output.Position = 0;

        return output;
    }

    /// <summary>
    /// Computes the CRC-32 bit by bit, independent of the production implementation.
    /// </summary>
    /// <param name="data">The data</param>
    /// <returns>The checksum</returns>
    internal static uint Reference(byte[] data)
    {
        var crc = 0xffffffffu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    /// <summary>
    /// Compresses the bytes as one plain member.
    /// </summary>
    /// <param name="plain">The content</param>
    /// <returns>Header, deflate data and trailer of <see cref="GZipStream"/></returns>
    private static byte[] Deflated(byte[] plain)
    {
        using var output = new MemoryStream();

        using (var zip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            zip.Write(plain);
        }

        var whole = output.ToArray();

        return whole.Length >= PlainHeaderLength + TrailerLength ? whole : throw new InvalidOperationException("gzip output too short");
    }

    #endregion // Methods
}