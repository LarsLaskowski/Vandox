using System.Security.Cryptography;

using Vandox.Storage;

namespace Vandox.Import.Tests;

/// <summary>
/// Computes the rows of line hashes the importer must store for a content, independently of the importer.
/// </summary>
internal static class ExpectedLineHashes
{
    #region Methods

    /// <summary>
    /// Returns the offsets just after every line feed of a content.
    /// </summary>
    /// <param name="content">The content</param>
    /// <returns>The line ends, ascending</returns>
    internal static List<long> LineEnds(byte[] content)
    {
        var ends = new List<long>();

        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == (byte)'\n')
            {
                ends.Add(index + 1L);
            }
        }

        return ends;
    }

    /// <summary>
    /// Returns the first 16 bytes of the SHA-256 of the bytes of a content up to every line end.
    /// </summary>
    /// <param name="content">The content</param>
    /// <returns>One hash per line end, in order</returns>
    internal static List<byte[]> Hashes(byte[] content)
    {
        return [.. LineEnds(content).Select(end => SHA256.HashData(content.AsSpan(0, (int)end))[..StorageLimits.LineHashBytes])];
    }

    /// <summary>
    /// Returns the rows of line hashes of a content: <paramref name="perRow"/> hashes per row, the first row from offset 0, every further row from the last line end of the row before, without file ID and attempt.
    /// </summary>
    /// <param name="content">The content</param>
    /// <param name="perRow">The most hashes in a row</param>
    /// <returns>The rows</returns>
    internal static List<ImportLineHashes> Rows(byte[] content, int perRow = StorageLimits.MaxLineHashes)
    {
        var ends = LineEnds(content);
        var hashes = Hashes(content);

        return Enumerable.Range(0, ends.Count)
                         .Chunk(perRow)
                         .Select(chunk => new ImportLineHashes
                                          {
                                              FirstOffset = chunk[0] == 0 ? 0 : ends[chunk[0] - 1],
                                              LastOffset = ends[chunk[^1]],
                                              Hashes = chunk.SelectMany(index => hashes[index]).ToArray()
                                          })
                         .ToList();
    }

    /// <summary>
    /// Describes rows as text, one row per line, for a comparison.
    /// </summary>
    /// <param name="rows">The rows</param>
    /// <returns>The text; file ID and attempt are part of it</returns>
    internal static string Describe(IEnumerable<ImportLineHashes> rows)
    {
        return string.Join('\n', rows.Select(row => $"file={row.FileId} attempt={row.Attempt} {row.FirstOffset}..{row.LastOffset} {Convert.ToHexString(row.Hashes)}"));
    }

    #endregion // Methods
}