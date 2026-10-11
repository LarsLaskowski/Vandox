using System.Text;

namespace Vandox.Core.Tests;

/// <summary>
/// Builds the record lines of the memory and time tests of the wire decoder: a valid metric record whose unknown envelope key
/// <c>x</c> holds a value of one of the shapes that make a duplicate-key check expensive.
/// </summary>
internal static class WireShapes
{
    #region Constants

    /// <summary>
    /// The size of the record line of a shape in bytes; the default line limit is 1 MiB.
    /// </summary>
    internal const int LineBytes = 1_000_000;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns the first keys that are distinct ignoring ASCII case and as short as possible: the printable ASCII characters other
    /// than the quote and the backslash, then pairs, then triples of them.
    /// </summary>
    /// <param name="count">The number of keys</param>
    /// <returns>The keys</returns>
    internal static List<string> ShortKeys(int count)
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
    /// Returns an object whose members are the keys, each with the value 0.
    /// </summary>
    /// <param name="keys">The keys</param>
    /// <returns>The JSON text</returns>
    internal static string ObjectOfKeys(IEnumerable<string> keys)
    {
        return string.Concat("{", string.Join(",", keys.Select(key => $"\"{key}\":0")), "}");
    }

    /// <summary>
    /// Returns an object of distinct short keys of about the given size.
    /// </summary>
    /// <param name="size">The size in bytes</param>
    /// <returns>The JSON text</returns>
    internal static string ObjectFillingBytes(int size)
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
    /// Returns a list that starts with <paramref name="first"/> (if not empty) and holds as many copies of <paramref name="unit"/> as fit into
    /// about the given size.
    /// </summary>
    /// <param name="first">The first element, or an empty text</param>
    /// <param name="unit">The repeated element</param>
    /// <param name="size">The size in bytes</param>
    /// <returns>The JSON text</returns>
    internal static string ListOf(string first, string unit, int size)
    {
        var builder = new StringBuilder("[");

        builder.Append(first);

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

    /// <summary>
    /// Returns the value of the key <c>x</c> of one of the shapes of a record line of about <see cref="LineBytes"/>.
    /// </summary>
    /// <param name="shape">The shape: 1 a list of small objects, 2 one object of distinct keys, 3 a list of empty arrays, 4 a list of numbers, 5 one object of distinct keys filling half the line followed by small objects, 6 a list of objects of <paramref name="keys"/> distinct keys</param>
    /// <param name="keys">The number of keys of the objects of shape 6</param>
    /// <returns>The JSON text</returns>
    internal static string Value(int shape, int keys)
    {
        return shape switch
               {
                   1 => ListOf(string.Empty, """{"a":0}""", LineBytes),
                   2 => ObjectFillingBytes(LineBytes),
                   3 => ListOf(string.Empty, "[]", LineBytes),
                   4 => ListOf(string.Empty, "0", LineBytes),
                   5 => ListOf(ObjectFillingBytes(LineBytes / 2), """{"a":0}""", LineBytes),
                   6 => ListOf(string.Empty, ObjectOfKeys(ShortKeys(keys)), LineBytes),
                   _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "no such shape")
               };
    }

    /// <summary>
    /// Returns a valid metric record line whose unknown envelope key <c>x</c> holds a value.
    /// </summary>
    /// <param name="seq">The sequence number</param>
    /// <param name="value">The JSON text of the value</param>
    /// <returns>The line</returns>
    internal static string Record(int seq, string value)
    {
        return string.Concat("{\"kind\":\"metric\",\"source\":\"host\",\"seq\":", seq, ",\"captured_at\":\"2026-10-01T10:00:00Z\",\"x\":", value, ",\"data\":{\"name\":\"cpu\",\"value\":1}}");
    }

    /// <summary>
    /// Returns a value of nested arrays.
    /// </summary>
    /// <param name="depth">The number of arrays</param>
    /// <returns>The JSON text</returns>
    internal static string Arrays(int depth)
    {
        return string.Concat(new string('[', depth), "0", new string(']', depth));
    }

    /// <summary>
    /// Returns a value of nested objects, each with the given members before the member with the empty key.
    /// </summary>
    /// <param name="depth">The number of objects</param>
    /// <param name="members">The members before the empty key, with a trailing comma, or an empty text</param>
    /// <returns>The JSON text</returns>
    internal static string Objects(int depth, string members)
    {
        return string.Concat(string.Concat(Enumerable.Repeat($"{{{members}\"\":", depth)), "0", new string('}', depth));
    }

    /// <summary>
    /// Returns a batch of about the given size of short records with increasing sequence numbers, each with the unknown key <c>x</c>.
    /// </summary>
    /// <param name="value">The JSON text of the value of <c>x</c></param>
    /// <param name="size">The size in bytes</param>
    /// <returns>The lines of the batch, the header first</returns>
    internal static List<string> Batch(string value, int size)
    {
        var lines = new List<string>
                    {
                        BatchBuilder.Header
                    };
        var total = BatchBuilder.Header.Length + 1;

        while (total < size)
        {
            var line = Record(lines.Count, value);

            lines.Add(line);
            total += line.Length + 1;
        }

        return lines;
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

    #endregion // Methods
}