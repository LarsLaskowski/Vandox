using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Vandox.Import.Tests;

/// <summary>
/// Builds import inputs: files, gzip files and tar archives.
/// </summary>
internal static class TestInputs
{
    #region Methods

    /// <summary>
    /// Returns log content with the given number of lines.
    /// </summary>
    /// <param name="lines">The number of lines</param>
    /// <param name="prefix">The text before the line number</param>
    /// <returns>The content, starting with <c>LOG</c></returns>
    internal static string Log(int lines, string prefix = "LOG line")
    {
        return string.Concat(Enumerable.Range(1, lines).Select(number => $"{prefix} {number}\n"));
    }

    /// <summary>
    /// Compresses bytes with gzip.
    /// </summary>
    /// <param name="content">The content</param>
    /// <returns>The compressed bytes</returns>
    internal static byte[] Gzip(string content)
    {
        return Gzip(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Compresses bytes with gzip.
    /// </summary>
    /// <param name="content">The content</param>
    /// <returns>The compressed bytes</returns>
    internal static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();

        using (var zip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            zip.Write(content);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds a tar archive of regular files.
    /// </summary>
    /// <param name="entries">The names and contents</param>
    /// <returns>The archive</returns>
    internal static byte[] Tar(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();

        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
                                  {
                                      DataStream = new MemoryStream(content)
                                  });
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds a tar archive with the given entries of any type.
    /// </summary>
    /// <param name="entries">The entries</param>
    /// <returns>The archive</returns>
    internal static byte[] Tar(params TarEntry[] entries)
    {
        using var output = new MemoryStream();

        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    /// <summary>
    /// Returns the bytes of a text.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The UTF-8 bytes</returns>
    internal static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    #endregion // Methods
}