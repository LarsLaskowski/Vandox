using System.Globalization;
using System.Text;

using Vandox.Backend.Ingest;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the request bodies of the memory tests: record lines of the hostile shapes that make a duplicate-key check expensive, a batch of distinct
/// log lines that fills the batch limit and a batch of the largest connection snapshots.
/// </summary>
internal static class IngestMemoryBodies
{
    #region Constants

    /// <summary>
    /// The size of a hostile record line in bytes; the line limit is 1 MiB.
    /// </summary>
    internal const int HostileLineBytes = 1_000_000;

    private const int SnapshotLineBytes = 1_040_000;

    private const int ListEntries = 4096;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Builds a batch of one valid metric record whose unknown envelope key <c>x</c> holds a value of a hostile shape.
    /// </summary>
    /// <param name="shape">The shape: <c>distinct keys</c> (one object), <c>objects of 24 keys</c> or <c>small objects</c> (lists)</param>
    /// <returns>The compressed batch and the length of the record line</returns>
    /// <exception cref="ArgumentException">The shape is unknown</exception>
    internal static (byte[] Body, int LineLength) HostileLine(string shape)
    {
        var value = shape switch
                    {
                        "distinct keys" => ObjectFillingBytes(HostileLineBytes),
                        "objects of 24 keys" => ListOf(ObjectOfKeys(ShortKeys(24)), HostileLineBytes),
                        "small objects" => ListOf("""{"a":0}""", HostileLineBytes),
                        _ => throw new ArgumentException($"unknown shape {shape}", nameof(shape))
                    };
        var line = IngestSamples.Metric(1, extraEnvelope: $"\"x\":{value},");

        return (IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), line])), line.Length);
    }

    /// <summary>
    /// Builds a batch of distinct log lines that fills the batch limit as far as the smallest log line allows.
    /// </summary>
    /// <returns>The compressed batch and the number of records</returns>
    internal static (byte[] Body, int Records) DistinctLogLines()
    {
        var lines = new List<string>
                    {
                        IngestSamples.Header()
                    };
        var total = lines[0].Length + 1L;

        for (var seq = 1; seq <= IngestLimits.MaxRecords; seq++)
        {
            var line = LogLine(seq);

            if (total + line.Length + 1 > IngestLimits.MaxBatchBytes)
            {
                break;
            }

            lines.Add(line);
            total += line.Length + 1;
        }

        return (IngestSamples.Gzip(IngestSamples.Plain(lines)), lines.Count - 1);
    }

    /// <summary>
    /// Builds a batch of two connection snapshots that each fill the line limit with entries of the five lists.
    /// </summary>
    /// <returns>The compressed batch and the length of the first snapshot line</returns>
    internal static (byte[] Body, int LineLength) LargestConnectionSnapshots()
    {
        var first = ConnectionSnapshot(1);
        var second = ConnectionSnapshot(2);

        return (IngestSamples.Gzip(IngestSamples.Plain([IngestSamples.Header(), first, second])), first.Length);
    }

    /// <summary>
    /// Returns a log line record whose message is a word of its own, so that no two lines share a template.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <returns>The line without its line feed</returns>
    private static string LogLine(int seq)
    {
        var word = new StringBuilder();

        for (var rest = seq; rest > 0; rest /= 20)
        {
            word.Append((char)('g' + (rest % 20)));
        }

        return $$$"""{"kind":"log_line","source":"j","seq":{{{seq.ToString(CultureInfo.InvariantCulture)}}},"captured_at":"{{{IngestSamples.CapturedAt}}}","data":{"log":"j","message":"{{{word}}}"}}""";
    }

    /// <summary>
    /// Returns a connection snapshot record line of about <see cref="SnapshotLineBytes"/> whose five lists hold as many compact entries as fit, at most 4,096
    /// each, with the rest of the line (if any) filled by the command names.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <returns>The line without its line feed</returns>
    private static string ConnectionSnapshot(int seq)
    {
        var full = ConnectionSnapshot(seq, ListEntries, 0);

        if (full.Length > SnapshotLineBytes)
        {
            return ConnectionSnapshot(seq, (int)((long)ListEntries * SnapshotLineBytes / full.Length) - 8, 0);
        }

        return ConnectionSnapshot(seq, ListEntries, (SnapshotLineBytes - full.Length) / (4 * ListEntries));
    }

    /// <summary>
    /// Returns a connection snapshot record line whose five lists hold the same number of entries.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="entries">The number of entries of each list</param>
    /// <param name="padding">The number of letters appended to every command name of the lists that have one</param>
    /// <returns>The line without its line feed</returns>
    private static string ConnectionSnapshot(int seq, int entries, int padding)
    {
        var tail = new string('c', padding);
        var connections = string.Join(',', Enumerable.Range(0, entries).Select(index => $$$"""{"proto":"tcp","local":"10.{{{seq}}}.{{{index / 256}}}.{{{index % 256}}}:443","remote":"192.0.{{{index / 256}}}.{{{index % 256}}}:1","state":"CLOSE","pid":{{{index + 1}}},"command":"c{{{tail}}}"}"""));
        var listeners = string.Join(',', Enumerable.Range(0, entries).Select(index => Listener(index, seq, tail)));
        var newListeners = string.Join(',', Enumerable.Range(ListEntries, entries).Select(index => Listener(index, seq, tail)));
        var processes = string.Join(',', Enumerable.Range(0, entries).Select(index => $$$"""{"pid":{{{index + 1}}},"command":"p{{{tail}}}","count":1}"""));
        var remotes = string.Join(',', Enumerable.Range(0, entries).Select(index => $$$"""{"addr":"10.{{{seq}}}.{{{index / 256}}}.{{{index % 256}}}","count":1}"""));
        var data = $$$"""{"complete":false,"aggregates_incomplete":true,"listener_check":"all","states":[{"proto":"tcp","state":"CLOSE","count":{{{entries}}}}],"processes":[{{{processes}}}],"remotes":[{{{remotes}}}],"listeners":[{{{listeners}}}],"connections":[{{{connections}}}],"new_listeners":[{{{newListeners}}}]}""";

        return $$$"""{"kind":"connection_snapshot","source":"connections","seq":{{{seq}}},"captured_at":"{{{IngestSamples.CapturedAt}}}","data":{{{data}}}}""";
    }

    /// <summary>
    /// Returns one listener entry.
    /// </summary>
    /// <param name="index">The index that makes the entry distinct</param>
    /// <param name="seq">The sequence number of the snapshot</param>
    /// <param name="tail">The letters appended to the command name</param>
    /// <returns>The JSON text</returns>
    private static string Listener(int index, int seq, string tail)
    {
        return $$$"""{"proto":"udp","local":"172.{{{seq}}}.{{{index / 256 % 256}}}.{{{index % 256}}}:1","pid":{{{index + 1}}},"command":"l{{{tail}}}"}""";
    }

    /// <summary>
    /// Returns the first keys that are distinct ignoring ASCII case and as short as possible.
    /// </summary>
    /// <param name="count">The number of keys</param>
    /// <returns>The keys</returns>
    private static List<string> ShortKeys(int count)
    {
        var alphabet = new List<string>();
        var seen = new HashSet<char>();

        for (var code = 0x20; code <= 0x7E; code++)
        {
            var character = (char)code;

            if (character != '"' && character != '\\' && seen.Add(char.ToLowerInvariant(character)))
            {
                alphabet.Add(character.ToString());
            }
        }

        return Enumerate(alphabet).Take(count).ToList();
    }

    /// <summary>
    /// Lists the single keys, then the pairs, then the triples of an alphabet.
    /// </summary>
    /// <param name="alphabet">The characters</param>
    /// <returns>The keys</returns>
    private static IEnumerable<string> Enumerate(List<string> alphabet)
    {
        foreach (var first in alphabet)
        {
            yield return first;
        }

        foreach (var first in alphabet)
        {
            foreach (var second in alphabet)
            {
                yield return $"{first}{second}";
            }
        }

        foreach (var first in alphabet)
        {
            foreach (var second in alphabet)
            {
                foreach (var third in alphabet)
                {
                    yield return $"{first}{second}{third}";
                }
            }
        }
    }

    /// <summary>
    /// Returns an object whose members are the keys, each with the value 0.
    /// </summary>
    /// <param name="keys">The keys</param>
    /// <returns>The JSON text</returns>
    private static string ObjectOfKeys(IEnumerable<string> keys)
    {
        return string.Concat("{", string.Join(",", keys.Select(key => $"\"{key}\":0")), "}");
    }

    /// <summary>
    /// Returns an object of distinct short keys of about the given size.
    /// </summary>
    /// <param name="size">The size in bytes</param>
    /// <returns>The JSON text</returns>
    private static string ObjectFillingBytes(int size)
    {
        var keys = ShortKeys(300_000);
        var length = 2;
        var count = 0;

        while (length < size)
        {
            length += keys[count].Length + 5;
            count++;
        }

        return ObjectOfKeys(keys.Take(count));
    }

    /// <summary>
    /// Returns a list that holds copies of a unit up to about the given size.
    /// </summary>
    /// <param name="unit">The repeated element</param>
    /// <param name="size">The size in bytes</param>
    /// <returns>The JSON text</returns>
    private static string ListOf(string unit, int size)
    {
        var builder = new StringBuilder("[");

        while (builder.Length < size)
        {
            if (builder.Length > 1)
            {
                builder.Append(',');
            }

            builder.Append(unit);
        }

        return builder.Append(']').ToString();
    }

    #endregion // Methods
}