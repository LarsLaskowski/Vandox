using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the gzip batches that the ingest tests post, and breaks them in the ways the tests need.
/// </summary>
internal static class IngestSamples
{
    #region Constants

    /// <summary>
    /// The agent ID of the golden batch, bound to the token by the tests.
    /// </summary>
    internal const string AgentId = "agent-1";

    /// <summary>
    /// The boot ID of the batches built here.
    /// </summary>
    internal const string BootId = "0b6f9b0c-2d1e-4c43-9a4e-7f1b2c3d4e5f";

    /// <summary>
    /// The capture time of every record built here.
    /// </summary>
    internal const string CapturedAt = "2026-10-01T10:00:00Z";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns a header line.
    /// </summary>
    /// <param name="agentId">The agent ID</param>
    /// <param name="mode">The mode</param>
    /// <param name="major">The format major</param>
    /// <param name="minor">The format minor</param>
    /// <param name="clockOffsetNs">The clock offset, <c>null</c> for none</param>
    /// <param name="extra">Text inserted into the object after the first key, such as an unknown key with a trailing comma; empty for none</param>
    /// <returns>The line without its line feed</returns>
    internal static string Header(string agentId = AgentId, string mode = "live", int major = 1, long minor = 0, long? clockOffsetNs = null, string extra = "")
    {
        var offset = clockOffsetNs is null ? string.Empty : $",\"clock_offset_ns\":{clockOffsetNs.Value.ToString(CultureInfo.InvariantCulture)}";

        return $$"""{"format_major":{{major}},{{extra}}"format_minor":{{minor}},"agent_id":"{{agentId}}","boot_id":"{{BootId}}","mode":"{{mode}}"{{offset}}}""";
    }

    /// <summary>
    /// Returns a metric record line.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="source">The source</param>
    /// <param name="value">The value</param>
    /// <param name="labels">The JSON text of the labels object, empty for none</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="extraData">Text inserted at the start of the data object, such as an unknown key with a trailing comma</param>
    /// <param name="extraEnvelope">Text inserted at the start of the record object, such as an unknown key with a trailing comma</param>
    /// <returns>The line without its line feed</returns>
    internal static string Metric(long seq, string source = "host", double value = 1.5, string labels = "", string capturedAt = CapturedAt, string extraData = "", string extraEnvelope = "")
    {
        var labelText = labels.Length == 0 ? string.Empty : $",\"labels\":{labels}";

        return $$$"""{{{{extraEnvelope}}}"kind":"metric","source":"{{{source}}}","seq":{{{seq}}},"captured_at":"{{{capturedAt}}}","data":{{{{extraData}}}"name":"cpu","value":{{{value.ToString(CultureInfo.InvariantCulture)}}}{{{labelText}}}}}""";
    }

    /// <summary>
    /// Returns the lines of a valid batch: a header and metric records with consecutive sequence numbers.
    /// </summary>
    /// <param name="first">The first sequence number</param>
    /// <param name="count">The number of records</param>
    /// <param name="agentId">The agent ID of the header</param>
    /// <param name="mode">The mode of the header</param>
    /// <returns>The lines</returns>
    internal static List<string> Lines(long first, int count, string agentId = AgentId, string mode = "live")
    {
        var lines = new List<string>
                    {
                        Header(agentId, mode)
                    };

        for (var index = 0; index < count; index++)
        {
            lines.Add(Metric(first + index));
        }

        return lines;
    }

    /// <summary>
    /// Joins lines, each followed by a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The text</returns>
    internal static string Plain(IEnumerable<string> lines)
    {
        return string.Concat(lines.Select(line => line + "\n"));
    }

    /// <summary>
    /// Compresses text into one gzip member.
    /// </summary>
    /// <param name="plain">The text</param>
    /// <returns>The compressed bytes</returns>
    internal static byte[] Gzip(string plain)
    {
        return Gzip(Encoding.UTF8.GetBytes(plain));
    }

    /// <summary>
    /// Compresses bytes into one gzip member.
    /// </summary>
    /// <param name="plain">The bytes</param>
    /// <returns>The compressed bytes</returns>
    internal static byte[] Gzip(byte[] plain)
    {
        using var output = new MemoryStream();

        using (var zip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            zip.Write(plain);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Returns a valid compressed batch.
    /// </summary>
    /// <param name="first">The first sequence number</param>
    /// <param name="count">The number of records</param>
    /// <param name="agentId">The agent ID of the header</param>
    /// <param name="mode">The mode of the header</param>
    /// <returns>The compressed bytes</returns>
    internal static byte[] Valid(long first = 1, int count = 3, string agentId = AgentId, string mode = "live")
    {
        return Gzip(Plain(Lines(first, count, agentId, mode)));
    }

    /// <summary>
    /// Returns the compressed bytes without their last bytes.
    /// </summary>
    /// <param name="compressed">The compressed bytes</param>
    /// <param name="cut">The number of bytes to cut</param>
    /// <returns>The shortened bytes</returns>
    internal static byte[] Truncated(byte[] compressed, int cut)
    {
        return compressed[..^cut];
    }

    /// <summary>
    /// Returns the compressed bytes with a changed CRC-32 in the trailer.
    /// </summary>
    /// <param name="compressed">The compressed bytes</param>
    /// <returns>The damaged bytes</returns>
    internal static byte[] WithWrongCrc(byte[] compressed)
    {
        var copy = (byte[])compressed.Clone();

        copy[^8] ^= 0xff;

        return copy;
    }

    /// <summary>
    /// Returns the compressed bytes followed by more bytes.
    /// </summary>
    /// <param name="compressed">The compressed bytes</param>
    /// <param name="trailing">The bytes after the last member</param>
    /// <returns>The longer bytes</returns>
    internal static byte[] WithTrailing(byte[] compressed, params byte[] trailing)
    {
        return [.. compressed, .. trailing];
    }

    #endregion // Methods
}