using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

using Vandox.Core.Configuration;
using Vandox.Core.Model;

namespace Vandox.Core.Signatures;

/// <summary>
/// Reads the YAML text of a signature catalog and applies the catalog rules. Error texts name the catalog, the line and the
/// rule, never the document text.
/// </summary>
internal sealed class SignatureCatalogReader
{
    #region Constants

    private const int MaxNameChars = 64;
    private const string IdRule = "^[a-z][a-z0-9_]{0,63}$";

    #endregion // Constants

    #region Fields

    private static readonly HashSet<string> _signatureKeys = new(["id", "description", "program", "source", "event", "message", "fields"], StringComparer.Ordinal);

    private readonly string _name;
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates a reader.
    /// </summary>
    /// <param name="name">The name of the text in error messages</param>
    internal SignatureCatalogReader(string name)
    {
        _name = name;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads and checks a catalog text.
    /// </summary>
    /// <param name="text">The YAML text</param>
    /// <returns>The signatures in document order</returns>
    /// <exception cref="SignatureException">The text breaks a catalog rule</exception>
    internal IReadOnlyList<Signature> Read(string text)
    {
        var root = ReadTree(text);

        if (root is null || root.IsNull)
        {
            throw Error("no signatures");
        }

        RejectAnchors(root);

        var list = ReadRoot(root);
        var signatures = new Signature[list.Content.Count];

        for (var index = 0; index < signatures.Length; index++)
        {
            signatures[index] = ReadSignature(list.Content[index]);
        }

        return Array.AsReadOnly(signatures);
    }

    /// <summary>
    /// Tells whether a text is a name of the catalog: a lower-case letter, then at most 63 lower-case letters, digits or underscores.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is a name</returns>
    private static bool IsName(string text)
    {
        if (text.Length is < 1 or > MaxNameChars || text[0] is < 'a' or > 'z')
        {
            return false;
        }

        return text.All(IsNameCharacter);
    }

    /// <summary>
    /// Tells whether a character may be part of a name of the catalog.
    /// </summary>
    /// <param name="character">The character</param>
    /// <returns><c>true</c> when the character is a lower-case letter, a digit or an underscore</returns>
    private static bool IsNameCharacter(char character)
    {
        return character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
    }

    /// <summary>
    /// Tells whether a text is a name in the wire format's pattern.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is a source or event name</returns>
    private static bool IsWireName(string text)
    {
        return text.Length <= ModelLimits.MaxNameBytes && Check.NamePattern().IsMatch(text);
    }

    /// <summary>
    /// Tells whether a text takes 1 to 1,024 UTF-8 bytes.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the length is in range</returns>
    private static bool IsShortText(string text)
    {
        return text.Length > 0 && Encoding.UTF8.GetByteCount(text) <= SignatureCatalog.MaxDescriptionBytes;
    }

    /// <summary>
    /// Reads the YAML tree.
    /// </summary>
    /// <param name="text">The YAML text</param>
    /// <returns>The root node, or <c>null</c> for a text without a document</returns>
    private YamlNode? ReadTree(string text)
    {
        try
        {
            return YamlTreeReader.Read(_name, text);
        }
        catch (ConfigException exception)
        {
            throw new SignatureException($"signatures: {_name}: not valid YAML", exception);
        }
    }

    /// <summary>
    /// Creates the error for a rule break without a line.
    /// </summary>
    /// <param name="reason">The rule</param>
    /// <returns>The exception</returns>
    private SignatureException Error(string reason)
    {
        return new SignatureException($"signatures: {_name}: {reason}");
    }

    /// <summary>
    /// Creates the error for a rule break at a line.
    /// </summary>
    /// <param name="line">The line</param>
    /// <param name="reason">The rule</param>
    /// <returns>The exception</returns>
    private SignatureException Error(int line, string reason)
    {
        return new SignatureException($"signatures: {_name}:{line}: {reason}");
    }

    /// <summary>
    /// Throws the error of a rule when its condition does not hold.
    /// </summary>
    /// <param name="condition">The condition the rule requires</param>
    /// <param name="line">The line</param>
    /// <param name="reason">The rule</param>
    private void Require(bool condition, int line, string reason)
    {
        if (condition)
        {
            return;
        }

        throw Error(line, reason);
    }

    /// <summary>
    /// Refuses an anchor, an alias or a tag anywhere in the tree.
    /// </summary>
    /// <param name="node">The node</param>
    private void RejectAnchors(YamlNode node)
    {
        if (node.Kind == YamlNodeKind.Alias || node.HasAnchor || node.Tag.Length > 0)
        {
            throw Error(node.Line, "anchors, aliases and tags are not supported");
        }

        foreach (var child in node.Content)
        {
            RejectAnchors(child);
        }
    }

    /// <summary>
    /// Reads the root mapping and returns its list of signatures.
    /// </summary>
    /// <param name="root">The root node</param>
    /// <returns>The sequence node of the signatures</returns>
    private YamlNode ReadRoot(YamlNode root)
    {
        if (root.Kind != YamlNodeKind.Mapping)
        {
            throw RootError(root.Line);
        }

        SignatureEntry? signatures = null;

        for (var index = 0; index + 1 < root.Content.Count; index += 2)
        {
            var entry = new SignatureEntry(root.Content[index], root.Content[index + 1]);

            if (entry.Name != "signatures")
            {
                throw RootError(entry.Line);
            }

            if (signatures is not null)
            {
                throw Error(entry.Line, "duplicate key");
            }

            signatures = entry;
        }

        if (signatures is null)
        {
            throw RootError(root.Line);
        }

        if (signatures.Value.Kind != YamlNodeKind.Sequence || signatures.Value.Content.Count == 0)
        {
            throw Error(signatures.Line, "signatures must be a non-empty list");
        }

        return signatures.Value;
    }

    /// <summary>
    /// Creates the error for a root that is no mapping with the single key signatures.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The exception</returns>
    private SignatureException RootError(int line)
    {
        return Error(line, "the document must be a mapping with the single key signatures");
    }

    /// <summary>
    /// Reads the entries of a mapping in document order.
    /// </summary>
    /// <param name="mapping">The mapping node</param>
    /// <param name="allowed">The allowed keys, or <c>null</c> when any key is read</param>
    /// <returns>The entries</returns>
    private List<SignatureEntry> ReadEntries(YamlNode mapping, HashSet<string>? allowed)
    {
        var entries = new List<SignatureEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index + 1 < mapping.Content.Count; index += 2)
        {
            var entry = new SignatureEntry(mapping.Content[index], mapping.Content[index + 1]);

            Require(names.Add(entry.Name), entry.Line, "duplicate key");
            Require(allowed is null || allowed.Contains(entry.Name), entry.Line, "unknown key");

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Reads and checks one signature.
    /// </summary>
    /// <param name="node">The node of the list entry</param>
    /// <returns>The signature</returns>
    private Signature ReadSignature(YamlNode node)
    {
        if (node.Kind != YamlNodeKind.Mapping)
        {
            throw Error(node.Line, "a signature must be a mapping");
        }

        var map = ReadEntries(node, _signatureKeys).ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        Require(map.ContainsKey("id"), node.Line, "id is required");
        Require(map.ContainsKey("description"), node.Line, "description is required");

        var id = ReadId(map["id"]);
        var description = ReadDescription(map["description"]);
        var programs = ReadList(map, "program", "program must be a list of 1 to 16 names of 1 to 1024 bytes", IsShortText);
        var sources = ReadList(map, "source", "source must be a list of 1 to 16 source names", IsWireName);
        var events = ReadList(map, "event", "event must be a list of 1 to 16 event names", IsWireName);
        var message = ReadMessage(map);

        if (events.Count == 0 && message.Pattern is null)
        {
            throw Error(node.Line, "a signature needs an event or a message");
        }

        var fields = ReadFields(map, message);

        return new Signature(id, description, programs, sources, events, message.Pattern, fields);
    }

    /// <summary>
    /// Reads the ID of a signature.
    /// </summary>
    /// <param name="entry">The entry of the key id</param>
    /// <returns>The ID</returns>
    private string ReadId(SignatureEntry entry)
    {
        Require(entry.Value.Kind == YamlNodeKind.Scalar && IsName(entry.Value.Value), entry.Line, $"id must match {IdRule}");
        Require(_ids.Add(entry.Value.Value), entry.Line, "duplicate id");

        return entry.Value.Value;
    }

    /// <summary>
    /// Reads the description of a signature.
    /// </summary>
    /// <param name="entry">The entry of the key description</param>
    /// <returns>The description</returns>
    private string ReadDescription(SignatureEntry entry)
    {
        Require(entry.Value.Kind == YamlNodeKind.Scalar && IsShortText(entry.Value.Value), entry.Line, "description must be text of 1 to 1024 bytes");

        return entry.Value.Value;
    }

    /// <summary>
    /// Reads a list of 1 to 16 scalars, or an empty list when the key is absent.
    /// </summary>
    /// <param name="map">The entries of the signature</param>
    /// <param name="key">The key</param>
    /// <param name="reason">The rule, for the error</param>
    /// <param name="isValid">Tells whether an entry is acceptable</param>
    /// <returns>The read-only list</returns>
    private ReadOnlyCollection<string> ReadList(Dictionary<string, SignatureEntry> map, string key, string reason, Func<string, bool> isValid)
    {
        if (map.TryGetValue(key, out var entry))
        {
            var items = entry.Value.Content;

            Require(entry.Value.Kind == YamlNodeKind.Sequence && items.Count is >= 1 and <= SignatureCatalog.MaxListEntries, entry.Line, reason);

            var values = new string[items.Count];

            for (var index = 0; index < values.Length; index++)
            {
                Require(items[index].Kind == YamlNodeKind.Scalar && isValid(items[index].Value), entry.Line, reason);

                values[index] = items[index].Value;
            }

            return Array.AsReadOnly(values);
        }

        return Array.AsReadOnly(Array.Empty<string>());
    }

    /// <summary>
    /// Reads and compiles the message pattern.
    /// </summary>
    /// <param name="map">The entries of the signature</param>
    /// <returns>The pattern and the line of its key, the pattern <c>null</c> when the key is absent</returns>
    private (Regex? Pattern, int Line) ReadMessage(Dictionary<string, SignatureEntry> map)
    {
        if (map.TryGetValue("message", out var entry))
        {
            Require(entry.Value.Kind == YamlNodeKind.Scalar && entry.Value.Value.Length is >= 1 and <= SignatureCatalog.MaxPatternChars, entry.Line, "message must be a pattern of 1 to 4096 characters");

            try
            {
                return (new Regex(entry.Value.Value, Signature.PatternOptions, Regex.InfiniteMatchTimeout), entry.Line);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw Error(entry.Line, "message is not a valid pattern");
            }
        }

        return (null, 0);
    }

    /// <summary>
    /// Reads the fields and compares them with the named groups of the pattern.
    /// </summary>
    /// <param name="map">The entries of the signature</param>
    /// <param name="message">The pattern and the line of its key</param>
    /// <returns>The read-only list of fields in document order</returns>
    private ReadOnlyCollection<SignatureField> ReadFields(Dictionary<string, SignatureEntry> map, (Regex? Pattern, int Line) message)
    {
        var fields = new List<SignatureField>();

        if (map.TryGetValue("fields", out var entry))
        {
            fields.AddRange(ReadFieldEntries(entry, message.Pattern));
        }

        if (message.Pattern is not null)
        {
            var names = fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);

            Require(message.Pattern.GetGroupNames().All(group => group == "0" || names.Contains(group)), message.Line, "a named group of message is not a field");
        }

        return Array.AsReadOnly(fields.ToArray());
    }

    /// <summary>
    /// Reads the mapping of field names to types.
    /// </summary>
    /// <param name="entry">The entry of the key fields</param>
    /// <param name="pattern">The pattern whose named groups the fields must be</param>
    /// <returns>The fields in document order</returns>
    private List<SignatureField> ReadFieldEntries(SignatureEntry entry, Regex? pattern)
    {
        if (pattern is null)
        {
            throw Error(entry.Line, "fields need a message");
        }

        var node = entry.Value;

        Require(node.Kind == YamlNodeKind.Mapping && node.Content.Count is >= 2 and <= SignatureCatalog.MaxFields * 2, entry.Line, "fields must map 1 to 8 names to types");

        var groups = pattern.GetGroupNames().ToHashSet(StringComparer.Ordinal);
        var fields = new List<SignatureField>();

        foreach (var field in ReadEntries(node, null))
        {
            Require(IsName(field.Name), field.Line, $"field name must match {IdRule}");

            var type = ReadFieldType(field);

            Require(groups.Contains(field.Name), field.Line, "field is not a named group of message");

            fields.Add(new SignatureField(field.Name, type));
        }

        return fields;
    }

    /// <summary>
    /// Reads the type of a field.
    /// </summary>
    /// <param name="field">The entry of the field</param>
    /// <returns>The type</returns>
    private SignatureFieldType ReadFieldType(SignatureEntry field)
    {
        var text = field.Value.Kind == YamlNodeKind.Scalar ? field.Value.Value : string.Empty;

        return text switch
               {
                   "text" => SignatureFieldType.Text,
                   "integer" => SignatureFieldType.Integer,
                   "kibibytes" => SignatureFieldType.Kibibytes,
                   _ => throw Error(field.Line, "field type must be text, integer or kibibytes")
               };
    }

    #endregion // Methods
}