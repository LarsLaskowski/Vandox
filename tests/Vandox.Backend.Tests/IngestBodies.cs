using System.Text;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the damaged and oversized request bodies that the ingest tests name by a short description.
/// </summary>
internal static class IngestBodies
{
    #region Constants

    /// <summary>
    /// A label object whose key breaks the name rule of the wire format.
    /// </summary>
    internal const string BadLabel = """{"bad key":"v"}""";

    private const int LargeLabelBytes = 900;

    private const int OverLineLimit = (1 << 20) + 1000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds the body that a description names.
    /// </summary>
    /// <param name="description">The description</param>
    /// <returns>The compressed bytes</returns>
    /// <exception cref="ArgumentException">The description is unknown</exception>
    internal static byte[] Build(string description)
    {
        return description switch
               {
                   "decompressed over 2 MiB" => Gzip(LargeBatch(2300)),
                   "20001 records" => Gzip(IngestSamples.Plain(IngestSamples.Lines(1, 20001))),
                   "line over 1 MiB" => Gzip([IngestSamples.Header(), IngestSamples.Metric(1, new string('a', OverLineLimit))]),
                   "header line over 1 MiB" => Gzip([IngestSamples.Header(extra: $"\"pad\":\"{new string('a', OverLineLimit)}\","), IngestSamples.Metric(1)]),
                   "1 KB body to a hostile line" => IngestSamples.Gzip($"{IngestSamples.Header()}\n{new string('a', OverLineLimit)}"),
                   "not gzip" => Encoding.UTF8.GetBytes("this is not a gzip stream at all"),
                   "empty" => [],
                   "truncated" => IngestSamples.Truncated(IngestSamples.Valid(1, 3), 12),
                   "bad CRC" => IngestSamples.WithWrongCrc(IngestSamples.Valid(1, 3)),
                   "trailing bytes" => IngestSamples.WithTrailing(IngestSamples.Valid(1, 3), new byte[20]),
                   "bad JSON" => Gzip([IngestSamples.Header(), IngestSamples.Metric(1), "{not json"]),
                   "key rule" => Gzip([IngestSamples.Header(), DuplicateKeyLine()]),
                   "unreadable header line" => Gzip(["not json", IngestSamples.Metric(1)]),
                   "field error" => Gzip([IngestSamples.Header(), IngestSamples.Metric(1, labels: BadLabel)]),
                   "unknown kind" => Gzip([IngestSamples.Header(), UnknownKindLine()]),
                   "no records" => Gzip([IngestSamples.Header()]),
                   "sequence" => Gzip([IngestSamples.Header(), IngestSamples.Metric(2), IngestSamples.Metric(2)]),
                   "capture time outside the storable range" => Gzip([IngestSamples.Header(), IngestSamples.Metric(1, capturedAt: "2300-01-01T00:00:00Z")]),
                   "invalid header" => Gzip([IngestSamples.Header("bad id!"), IngestSamples.Metric(1)]),
                   "unsupported major" => Gzip([IngestSamples.Header(major: 2), IngestSamples.Metric(1)]),
                   "bad JSON after records" => Gzip([.. IngestSamples.Lines(1, 50), "{broken"]),
                   "bad CRC after records" => IngestSamples.WithWrongCrc(IngestSamples.Valid(1, 50)),
                   "field error after records" => Gzip([.. IngestSamples.Lines(1, 49), IngestSamples.Metric(50, labels: BadLabel)]),
                   "sequence after records" => Gzip([.. IngestSamples.Lines(1, 49), IngestSamples.Metric(10)]),
                   _ => throw new ArgumentException($"unknown body description {description}", nameof(description))
               };
    }

    /// <summary>
    /// Returns a record line whose keys repeat one name in two cases.
    /// </summary>
    /// <returns>The line</returns>
    private static string DuplicateKeyLine()
    {
        return $$$"""{"kind":"metric","KIND":"metric","source":"host","seq":1,"captured_at":"{{{IngestSamples.CapturedAt}}}","data":{"name":"cpu","value":1}}""";
    }

    /// <summary>
    /// Returns a record line of a kind the format does not know.
    /// </summary>
    /// <returns>The line</returns>
    private static string UnknownKindLine()
    {
        return $$$"""{"kind":"nope","source":"host","seq":1,"captured_at":"{{{IngestSamples.CapturedAt}}}","data":{}}""";
    }

    /// <summary>
    /// Returns the text of a valid batch of large records.
    /// </summary>
    /// <param name="records">The number of records</param>
    /// <returns>The text</returns>
    private static string LargeBatch(int records)
    {
        var lines = new List<string>
                    {
                        IngestSamples.Header()
                    };
        var labels = $$"""{"k":"{{new string('a', LargeLabelBytes)}}"}""";

        for (var index = 1; index <= records; index++)
        {
            lines.Add(IngestSamples.Metric(index, labels: labels));
        }

        return IngestSamples.Plain(lines);
    }

    /// <summary>
    /// Compresses lines, each followed by a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The compressed bytes</returns>
    private static byte[] Gzip(IEnumerable<string> lines)
    {
        return IngestSamples.Gzip(IngestSamples.Plain(lines));
    }

    /// <summary>
    /// Compresses text as it is.
    /// </summary>
    /// <param name="plain">The text</param>
    /// <returns>The compressed bytes</returns>
    private static byte[] Gzip(string plain)
    {
        return IngestSamples.Gzip(plain);
    }

    #endregion // Methods
}