using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds log line records and runs a signature detector over them for the signature tests.
/// </summary>
internal static class SignatureSamples
{
    #region Fields

    /// <summary>
    /// The capture time that tests count from.
    /// </summary>
    internal static readonly DateTimeOffset Base = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    #endregion // Fields

    #region Methods

    /// <summary>
    /// Creates an imported record that holds one log line.
    /// </summary>
    /// <param name="message">The message</param>
    /// <param name="source">The source of the record</param>
    /// <param name="program">The program, or empty</param>
    /// <param name="host">The host, or empty</param>
    /// <param name="eventName">The event, or empty</param>
    /// <param name="capturedAt">The capture time, or <see cref="Base"/></param>
    /// <returns>The record</returns>
    internal static DataRecord Line(string message, string source = "syslog", string program = "", string host = "", string eventName = "", DateTimeOffset? capturedAt = null)
    {
        return new DataRecord
               {
                   Origin = RecordOrigin.Import,
                   Source = source,
                   CapturedAt = capturedAt ?? Base,
                   Data = new LogLine
                          {
                              Log = "test.log",
                              Host = host,
                              Program = program,
                              Message = message,
                              Event = eventName
                          }
               };
    }

    /// <summary>
    /// Runs a detector over records numbered from 1 and completes it.
    /// </summary>
    /// <param name="catalog">The catalog</param>
    /// <param name="records">The records in the order the store returns them</param>
    /// <returns>The detected events in the order the detector emitted them</returns>
    internal static List<DetectedEvent> Run(SignatureCatalog catalog, params DataRecord[] records)
    {
        return Run(catalog, records.Select((record, index) => ((long)index + 1, record)));
    }

    /// <summary>
    /// Runs a detector over records with their IDs and completes it.
    /// </summary>
    /// <param name="catalog">The catalog</param>
    /// <param name="records">The records with their IDs in the order the store returns them</param>
    /// <returns>The detected events in the order the detector emitted them</returns>
    internal static List<DetectedEvent> Run(SignatureCatalog catalog, IEnumerable<(long Id, DataRecord Record)> records)
    {
        var detector = new SignatureDetector(catalog);
        var events = new List<DetectedEvent>();

        foreach (var (id, record) in records)
        {
            detector.Add(id, record, events);
        }

        detector.Complete(events);

        return events;
    }

    /// <summary>
    /// Parses a catalog text.
    /// </summary>
    /// <param name="lines">The lines of the YAML text</param>
    /// <returns>The catalog</returns>
    internal static SignatureCatalog Catalog(params string[] lines)
    {
        return SignatureCatalog.Parse("test.yaml", Text(lines));
    }

    /// <summary>
    /// Joins lines into a text that ends with a line feed.
    /// </summary>
    /// <param name="lines">The lines</param>
    /// <returns>The text</returns>
    internal static string Text(params string[] lines)
    {
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// Describes the fields of an event as sorted lines <c>t:name=value</c> and <c>n:name=value</c>.
    /// </summary>
    /// <param name="detected">The event</param>
    /// <returns>The description, empty for an event without fields</returns>
    internal static string Fields(DetectedEvent detected)
    {
        var entries = detected.Texts.Select(pair => $"t:{pair.Key}={pair.Value}").Concat(detected.Numbers.Select(pair => $"n:{pair.Key}={pair.Value}"));

        return string.Join('\n', entries.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Sorts the lines of an expected field description the way <see cref="Fields"/> does.
    /// </summary>
    /// <param name="expected">The lines <c>t:name=value</c> and <c>n:name=value</c> separated by line feeds</param>
    /// <returns>The sorted description</returns>
    internal static string Expected(string expected)
    {
        return string.Join('\n', expected.Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
    }

    #endregion // Methods
}