using Vandox.Core.Configuration;
using Vandox.Core.Signatures;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SignatureCatalog"/>
/// </summary>
[TestClass]
public class SignatureCatalogTests
{
    #region Constants

    private const string Head = "signatures:\n  - id: a\n    description: d\n";
    private const string OneEvent = "    event: [x.y]\n";
    private const string GroupA = "    message: '(?<a>x)'\n";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Parse reads every key of the schema: ID, description, programs, sources, events, the pattern options and the fields with their types in document order.
    /// </summary>
    [TestMethod]
    public void SignatureCatalogParseReadsEveryKey()
    {
        // Act
        var catalog = SignatureSamples.Catalog("signatures:",
                                               "  - id: first_one",
                                               "    description: The first.",
                                               "    program: [kernel, systemd]",
                                               "    source: [syslog, journal]",
                                               "    event: [e.one, e.two]",
                                               "    message: '^(?<a>[a-z]+) (?<b>[0-9]+) (?<c>[0-9]+)'",
                                               "    fields:",
                                               "      c: kibibytes",
                                               "      a: text",
                                               "      b: integer",
                                               "  - id: second_one",
                                               "    description: The second.",
                                               "    event: [e.three]");

        // Assert
        Assert.HasCount(2, catalog.Signatures, "two signatures");

        var first = catalog.Signatures[0];
        var second = catalog.Signatures[1];

        Assert.AreEqual("first_one", first.Id, "id");
        Assert.AreEqual("The first.", first.Description, "description");
        Assert.AreSequenceEqual(["kernel", "systemd"], first.Programs.ToList(), "programs");
        Assert.AreSequenceEqual(["syslog", "journal"], first.Sources.ToList(), "sources");
        Assert.AreSequenceEqual(["e.one", "e.two"], first.Events.ToList(), "events");
        Assert.IsNotNull(first.Pattern, "pattern");
        Assert.AreEqual("^(?<a>[a-z]+) (?<b>[0-9]+) (?<c>[0-9]+)", first.Pattern.ToString(), "pattern text");
        Assert.AreEqual(Signature.PatternOptions, first.Pattern.Options, "pattern options");
        Assert.AreSequenceEqual(["c", "a", "b"], first.Fields.Select(field => field.Name).ToList(), "field names in document order");
        Assert.AreSequenceEqual([SignatureFieldType.Kibibytes, SignatureFieldType.Text, SignatureFieldType.Integer], first.Fields.Select(field => field.Type).ToList(), "field types");
        Assert.AreEqual("second_one", second.Id, "second id");
        Assert.IsEmpty(second.Programs, "no programs");
        Assert.IsEmpty(second.Sources, "no sources");
        Assert.IsEmpty(second.Fields, "no fields");
        Assert.IsNull(second.Pattern, "no pattern");
        Assert.AreSequenceEqual(["e.three"], second.Events.ToList(), "events of the second");
    }

    /// <summary>
    /// A signature the engine has never seen is one entry of the catalog and needs no code: the detector turns its line into an event.
    /// </summary>
    [TestMethod]
    public void SignatureCatalogParseAcceptsASignatureTheEngineHasNeverSeen()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:",
                                               "  - id: segfault",
                                               "    description: A process crashed with a segmentation fault.",
                                               "    program: [kernel]",
                                               "    message: '^(?<command>[^ \\[\\n]+)\\[(?<pid>[0-9]+)\\]: segfault at '",
                                               "    fields:",
                                               "      command: text",
                                               "      pid: integer");

        // Act
        var events = SignatureSamples.Run(catalog, SignatureSamples.Line("nginx[1234]: segfault at 0 ip 00007f sp 00007f error 4 in libc.so.6", program: "kernel"));

        // Assert
        Assert.HasCount(1, events, "one event");
        Assert.AreEqual("segfault", events[0].SignatureId, "signature");
        Assert.AreEqual(SignatureSamples.Expected("t:command=nginx\nn:pid=1234"), SignatureSamples.Fields(events[0]), "fields");
    }

    /// <summary>
    /// Every list of a catalog is read-only and cannot be cast back to a list or an array, also when it is empty.
    /// </summary>
    [TestMethod]
    public void SignatureCatalogListsAreReadOnly()
    {
        // Arrange
        var catalog = SignatureSamples.Catalog("signatures:",
                                               "  - id: full",
                                               "    description: d",
                                               "    program: [p]",
                                               "    source: [s]",
                                               "    event: [e.one]",
                                               "    message: '(?<a>x)'",
                                               "    fields:",
                                               "      a: text",
                                               "  - id: bare",
                                               "    description: d",
                                               "    event: [e.one]");

        // Assert
        AssertReadOnly(catalog.Signatures, "signatures");

        foreach (var signature in catalog.Signatures)
        {
            AssertReadOnly(signature.Programs, $"programs of {signature.Id}");
            AssertReadOnly(signature.Sources, $"sources of {signature.Id}");
            AssertReadOnly(signature.Events, $"events of {signature.Id}");
            AssertReadOnly(signature.Fields, $"fields of {signature.Id}");
        }
    }

    /// <summary>
    /// Parse refuses each rule break with a signature exception and the fixed text that names the catalog, the line and the rule.
    /// </summary>
    /// <param name="label">What breaks the rule</param>
    /// <param name="text">The catalog text</param>
    /// <param name="expected">The expected message</param>
    [TestMethod]
    [DynamicData(nameof(InvalidCatalogs))]
    public void SignatureCatalogParseRefusesInvalidCatalogs(string label, string text, string expected)
    {
        // Act
        var exception = Assert.ThrowsExactly<SignatureException>(() => SignatureCatalog.Parse("test.yaml", text), label);

        // Assert
        Assert.AreEqual(expected, exception.Message, label);
    }

    /// <summary>
    /// A text that is not valid YAML keeps the configuration error of the YAML reader as the inner exception.
    /// </summary>
    [TestMethod]
    public void SignatureCatalogParseKeepsTheYamlErrorAsInner()
    {
        // Act
        var exception = Assert.ThrowsExactly<SignatureException>(() => SignatureCatalog.Parse("test.yaml", "signatures: [\n"), "syntax error");

        // Assert
        Assert.AreEqual("signatures: test.yaml: not valid YAML", exception.Message, "message");
        Assert.IsInstanceOfType<ConfigException>(exception.InnerException, "inner exception");
    }

    /// <summary>
    /// Gets the invalid catalogs: a label, the text and the message that Parse gives.
    /// </summary>
    /// <returns>The cases</returns>
    private static IEnumerable<object[]> InvalidCatalogs()
    {
        const string yaml = "signatures: test.yaml: not valid YAML";
        const string noSignatures = "signatures: test.yaml: no signatures";
        const string list = "signatures: test.yaml:1: signatures must be a non-empty list";
        const string eventList = "signatures: test.yaml:4: event must be a list of 1 to 16 event names";
        const string patternMessage = "signatures: test.yaml:4: message is not a valid pattern";
        const string description = "signatures: test.yaml:3: description must be text of 1 to 1024 bytes";
        const string program = "signatures: test.yaml:4: program must be a list of 1 to 16 names of 1 to 1024 bytes";
        const string source = "signatures: test.yaml:4: source must be a list of 1 to 16 source names";
        const string message = "signatures: test.yaml:4: message must be a pattern of 1 to 4096 characters";
        const string fields = "signatures: test.yaml:5: fields must map 1 to 8 names to types";
        const string fieldName = "signatures: test.yaml:6: field name must match ^[a-z][a-z0-9_]{0,63}$";
        const string fieldType = "signatures: test.yaml:6: field type must be text, integer or kibibytes";
        const string id = "signatures: test.yaml:2: id must match ^[a-z][a-z0-9_]{0,63}$";

        var valid = Doc(OneEvent);
        var many = string.Join(", ", Enumerable.Range(1, 17).Select(number => $"n{number}"));
        var nine = string.Concat(Enumerable.Range(1, 9).Select(number => $"      f{number}: text\n"));
        var longName = new string('a', 65);
        var longText = new string('a', 1025);
        var accented = new string((char)0xE9, 513);
        var longPattern = new string('a', 4097);

        yield return Case("syntax error", "signatures: [\n", yaml);
        yield return Case("second document", $"{valid}---\n{valid}", yaml);
        yield return Case("nesting deeper than 64", $"signatures: {new string('[', 70)}{new string(']', 70)}\n", yaml);

        yield return Case("empty text", string.Empty, noSignatures);
        yield return Case("comments only", "# nothing\n", noSignatures);
        yield return Case("empty document", "---\n", noSignatures);
        yield return Case("null root", "~\n", noSignatures);

        yield return Case("root is a sequence", "- a\n", Root(1));
        yield return Case("root is a scalar", "text\n", Root(1));
        yield return Case("empty root mapping", "{}\n", Root(1));
        yield return Case("other root key", $"{valid}other: 1\n", Root(5));
        yield return Case("no key signatures", "other: 1\n", Root(1));

        yield return Case("signatures is a scalar", "signatures: x\n", list);
        yield return Case("signatures is empty", "signatures: []\n", list);
        yield return Case("signatures is a mapping", "signatures:\n  a: b\n", list);
        yield return Case("signatures is null", "signatures:\n", list);

        yield return Case("entry is a scalar", "signatures:\n  - x\n", "signatures: test.yaml:2: a signature must be a mapping");

        yield return Case("tag on a value", $"signatures:\n  - id: !!str a\n    description: d\n{OneEvent}", Anchors(2));
        yield return Case("tag on a list", Doc("    event: !!seq [x.y]\n"), Anchors(4));
        yield return Case("anchor on a value", $"signatures:\n  - id: &a a\n    description: d\n{OneEvent}", Anchors(2));
        yield return Case("anchor and alias", $"signatures:\n  - id: &a a\n    description: *a\n{OneEvent}", Anchors(2));

        yield return Case("duplicate key in a signature", $"signatures:\n  - id: a\n    id: b\n    description: d\n{OneEvent}", "signatures: test.yaml:3: duplicate key");
        yield return Case("duplicate key in the root", $"{valid}signatures: []\n", "signatures: test.yaml:5: duplicate key");
        yield return Case("unknown key", Doc($"{OneEvent}    colour: red\n"), "signatures: test.yaml:5: unknown key");

        yield return Case("no id", $"signatures:\n  - description: d\n{OneEvent}", "signatures: test.yaml:2: id is required");
        yield return Case("no description", $"signatures:\n  - id: a\n{OneEvent}", "signatures: test.yaml:2: description is required");

        yield return Case("id with an upper case letter", Named("Abc"), id);
        yield return Case("id that starts with a digit", Named("1a"), id);
        yield return Case("id with a dash", Named("a-b"), id);
        yield return Case("id of 65 characters", Named(longName), id);
        yield return Case("id that is a list", Named("[a]"), id);
        yield return Case("id used before", $"{valid}  - id: a\n    description: d\n{OneEvent}", "signatures: test.yaml:5: duplicate id");

        yield return Case("empty description", $"signatures:\n  - id: a\n    description: ''\n{OneEvent}", description);
        yield return Case("description of 1025 bytes", $"signatures:\n  - id: a\n    description: {longText}\n{OneEvent}", description);
        yield return Case("description of 1026 bytes in 513 characters", $"signatures:\n  - id: a\n    description: {accented}\n{OneEvent}", description);
        yield return Case("description that is a mapping", $"signatures:\n  - id: a\n    description: {{a: b}}\n{OneEvent}", description);

        yield return Case("program is a scalar", Doc($"    program: x\n{OneEvent}"), program);
        yield return Case("program is empty", Doc($"    program: []\n{OneEvent}"), program);
        yield return Case("program of 17 entries", Doc($"    program: [{many}]\n{OneEvent}"), program);
        yield return Case("program with an empty entry", Doc($"    program: ['']\n{OneEvent}"), program);
        yield return Case("program with an entry of 1025 bytes", Doc($"    program: ['{longText}']\n{OneEvent}"), program);
        yield return Case("program with a mapping entry", Doc($"    program: [{{a: b}}]\n{OneEvent}"), program);

        yield return Case("source is a scalar", Doc($"    source: x\n{OneEvent}"), source);
        yield return Case("source is empty", Doc($"    source: []\n{OneEvent}"), source);
        yield return Case("source of 17 entries", Doc($"    source: [{many}]\n{OneEvent}"), source);
        yield return Case("source with a space", Doc($"    source: ['a b']\n{OneEvent}"), source);
        yield return Case("source with an empty entry", Doc($"    source: ['']\n{OneEvent}"), source);
        yield return Case("source that starts with a dash", Doc($"    source: ['-a']\n{OneEvent}"), source);

        yield return Case("event is a scalar", Doc("    event: x\n"), eventList);
        yield return Case("event is empty", Doc("    event: []\n"), eventList);
        yield return Case("event of 17 entries", Doc($"    event: [{many}]\n"), eventList);
        yield return Case("event with a space", Doc("    event: ['a b']\n"), eventList);
        yield return Case("event with an exclamation mark", Doc("    event: ['a!']\n"), eventList);

        yield return Case("empty message", Doc("    message: ''\n"), message);
        yield return Case("message of 4097 characters", Doc($"    message: '{longPattern}'\n"), message);
        yield return Case("message that is a list", Doc("    message: [a]\n"), message);

        yield return Case("lookahead", Doc("    message: '(?=a)b'\n"), patternMessage);
        yield return Case("lookbehind", Doc("    message: '(?<=a)b'\n"), patternMessage);
        yield return Case("atomic group", Doc("    message: '(?>a)'\n"), patternMessage);
        yield return Case("backreference to an existing group", Doc("    message: '(?<a>x)\\k<a>'\n    fields:\n      a: text\n"), patternMessage);
        yield return Case("conditional", Doc("    message: '(?<a>x)?(?(a)y|z)'\n    fields:\n      a: text\n"), patternMessage);
        yield return Case("unbalanced parenthesis", Doc("    message: '('\n"), patternMessage);

        yield return Case("neither event nor message", Doc(string.Empty), "signatures: test.yaml:2: a signature needs an event or a message");

        yield return Case("fields without a message", Doc($"{OneEvent}    fields:\n      a: text\n"), "signatures: test.yaml:5: fields need a message");

        yield return Case("fields is a scalar", Doc($"{GroupA}    fields: x\n"), fields);
        yield return Case("fields is a list", Doc($"{GroupA}    fields: []\n"), fields);
        yield return Case("fields is empty", Doc($"{GroupA}    fields: {{}}\n"), fields);
        yield return Case("fields has nine entries", Doc($"{GroupA}    fields:\n{nine}"), fields);

        yield return Case("field name with an upper case letter", Doc("    message: '(?<A>x)'\n    fields:\n      A: text\n"), fieldName);
        yield return Case("field name that starts with a digit", Doc("    message: 'x'\n    fields:\n      1a: text\n"), fieldName);
        yield return Case("field name of 65 characters", Doc($"    message: 'x'\n    fields:\n      {longName}: text\n"), fieldName);

        yield return Case("field type number", Doc($"{GroupA}    fields:\n      a: number\n"), fieldType);
        yield return Case("field type that is a list", Doc($"{GroupA}    fields:\n      a: [text]\n"), fieldType);
        yield return Case("field type of another case", Doc($"{GroupA}    fields:\n      a: Text\n"), fieldType);

        yield return Case("field that is no group", Doc("    message: 'x'\n    fields:\n      a: text\n"), "signatures: test.yaml:6: field is not a named group of message");
        yield return Case("group that is no field", Doc("    message: '(?<a>x)(?<b>y)'\n    fields:\n      a: text\n"), "signatures: test.yaml:4: a named group of message is not a field");
        yield return Case("group without any fields key", Doc(GroupA), "signatures: test.yaml:4: a named group of message is not a field");
    }

    /// <summary>
    /// Creates a catalog text of one signature with the ID <c>a</c> and the description <c>d</c> followed by more lines.
    /// </summary>
    /// <param name="tail">The further lines of the signature, each with its line feed</param>
    /// <returns>The text</returns>
    private static string Doc(string tail)
    {
        return $"{Head}{tail}";
    }

    /// <summary>
    /// Creates a catalog text of one signature with a given ID value.
    /// </summary>
    /// <param name="idValue">The YAML value of the key id</param>
    /// <returns>The text</returns>
    private static string Named(string idValue)
    {
        return $"signatures:\n  - id: {idValue}\n    description: d\n{OneEvent}";
    }

    /// <summary>
    /// Gives the message of the error about a root that is no mapping with the key signatures.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The message</returns>
    private static string Root(int line)
    {
        return $"signatures: test.yaml:{line}: the document must be a mapping with the single key signatures";
    }

    /// <summary>
    /// Gives the message of the error about an anchor, an alias or a tag.
    /// </summary>
    /// <param name="line">The line</param>
    /// <returns>The message</returns>
    private static string Anchors(int line)
    {
        return $"signatures: test.yaml:{line}: anchors, aliases and tags are not supported";
    }

    /// <summary>
    /// Creates the arguments of one invalid catalog case.
    /// </summary>
    /// <param name="label">What breaks the rule</param>
    /// <param name="text">The catalog text</param>
    /// <param name="expected">The expected message</param>
    /// <returns>The arguments</returns>
    private static object[] Case(string label, string text, string expected)
    {
        return [label, text, expected];
    }

    /// <summary>
    /// Asserts that a list cannot be changed by a cast to a list, an array or the mutable list interface.
    /// </summary>
    /// <typeparam name="T">The type of the entries</typeparam>
    /// <param name="list">The list</param>
    /// <param name="name">What the list is, for the messages</param>
    private static void AssertReadOnly<T>(IReadOnlyList<T> list, string name)
    {
        Assert.IsNotInstanceOfType<List<T>>(list, $"{name} is no List");
        Assert.IsNotInstanceOfType<T[]>(list, $"{name} is no array");

        if (list is IList<T> mutable)
        {
            Assert.IsTrue(mutable.IsReadOnly, $"{name} reports read-only");
            Assert.ThrowsExactly<NotSupportedException>(() => mutable.Add(default!), $"{name} refuses an added entry");
        }
    }

    #endregion // Methods
}