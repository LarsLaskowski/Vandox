using System.IO.Compression;
using System.Text;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds gzip-compressed batches for the decoder tests.
/// </summary>
internal static class BatchBuilder
{
    #region Constants

    /// <summary>
    /// A valid header line.
    /// </summary>
    internal const string Header = """{"format_major":1,"format_minor":0,"agent_id":"agent-1","boot_id":"0123abcd-0123-0123-0123-0123456789ab","mode":"live"}""";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns a valid metric record line.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <returns>The line</returns>
    internal static string Metric(int seq)
    {
        return Metric(seq, "host");
    }

    /// <summary>
    /// Returns a valid metric record line from the given source.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="source">The source</param>
    /// <returns>The line</returns>
    internal static string Metric(int seq, string source)
    {
        return $$$"""{"kind":"metric","source":"{{{source}}}","seq":{{{seq}}},"captured_at":"2026-10-01T10:00:00Z","data":{"name":"cpu","value":1.5}}""";
    }

    /// <summary>
    /// Compresses the lines, each followed by a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The compressed stream, positioned at its start</returns>
    internal static MemoryStream Gzip(params string[] lines)
    {
        return Gzip(string.Concat(lines.Select(line => line + "\n")));
    }

    /// <summary>
    /// Compresses the text as it is.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The compressed stream, positioned at its start</returns>
    internal static MemoryStream Gzip(string text)
    {
        var output = new MemoryStream();

        using (var zip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            zip.Write(Encoding.UTF8.GetBytes(text));
        }

        output.Position = 0;

        return output;
    }

    #endregion // Methods
}