using System.Globalization;
using System.Reflection;
using System.Text;

namespace Vandox.Core.Configuration;

/// <summary>
/// Decodes the single YAML document of a configuration file into an option object, strictly: an unknown or duplicate key,
/// a second document, an anchor, an alias, a custom tag or an invalid value is an error that names file, line and key and
/// never echoes the document text, so a secret pasted into the wrong place does not reach a log.
/// </summary>
internal sealed class StrictYamlDecoder
{
    #region Constants

    private const string SecretHint = "; secrets are read only from environment variables, never from this file";
    private const int MaxShownKeyBytes = 31;

    #endregion // Constants

    #region Fields

    private static readonly string[] _secretWords = ["token", "password", "secret"];

    private readonly string _file;
    private readonly Dictionary<string, int> _lines = new(StringComparer.Ordinal);

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="StrictYamlDecoder"/> class.
    /// </summary>
    /// <param name="file">The path of the file, for error messages</param>
    private StrictYamlDecoder(string file)
    {
        _file = file;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Decodes <paramref name="text"/> into <paramref name="target"/>, whose properties already hold the defaults.
    /// </summary>
    /// <param name="file">The path of the file, for error messages</param>
    /// <param name="text">The YAML text</param>
    /// <param name="target">The option object; its sections and options carry <see cref="ConfigKeyAttribute"/></param>
    /// <returns>The line of every key the file set, by dotted path</returns>
    /// <exception cref="ConfigException">The document is invalid</exception>
    internal static Dictionary<string, int> Decode(string file, string text, object target)
    {
        var decoder = new StrictYamlDecoder(file);
        var root = YamlTreeReader.Read(file, text);

        if (root is null)
        {
            return decoder._lines;
        }

        decoder.CheckNode(string.Empty, root);

        if (root.IsNull)
        {
            return decoder._lines;
        }

        if (root.Kind != YamlNodeKind.Mapping)
        {
            throw ConfigException.ForKey(file, root.Line, string.Empty, "top level must be a mapping");
        }

        decoder.WalkMapping(string.Empty, root, target);

        return decoder._lines;
    }

    /// <summary>
    /// Tells whether an unknown key name may be echoed: 1 to 31 characters of <c>[A-Za-z0-9_-]</c>. That is shorter than an
    /// agent token and excludes control characters, dots and the characters of secret formats.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns><c>true</c> when the name may be shown</returns>
    internal static bool IsSafeName(string name)
    {
        return name.Length is >= 1 and <= MaxShownKeyBytes && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_' || character == '-');
    }

    /// <summary>
    /// Tells whether a text is free of control, format and line-separating characters.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is clean</returns>
    internal static bool IsCleanString(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps the key names of an option type to its properties.
    /// </summary>
    /// <param name="type">The type of a section</param>
    /// <returns>The properties by key name</returns>
    private static Dictionary<string, PropertyInfo> OptionProperties(Type type)
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = property.GetCustomAttribute<ConfigKeyAttribute>();

            if (attribute is not null)
            {
                result[attribute.Name] = property;
            }
        }

        return result;
    }

    /// <summary>
    /// Appends a name to a dotted path.
    /// </summary>
    /// <param name="prefix">The path; empty at the top level</param>
    /// <param name="name">The name</param>
    /// <returns>The joined path</returns>
    private static string JoinPath(string prefix, string name)
    {
        return prefix.Length == 0 ? name : $"{prefix}.{name}";
    }

    /// <summary>
    /// Applies the rules every node must satisfy: no anchor or alias, an allowed tag, and a null tag only on a null spelling.
    /// </summary>
    /// <param name="key">The dotted key path reported with the error</param>
    /// <param name="node">The node</param>
    private void CheckNode(string key, YamlNode node)
    {
        if (node.Kind == YamlNodeKind.Alias || node.HasAnchor)
        {
            throw ConfigException.ForKey(_file, node.Line, key, "anchors and aliases are not supported");
        }

        if (node.Kind == YamlNodeKind.Scalar && node.Tag.Length == 0 && node.IsPlain && node.Value == "<<")
        {
            throw ConfigException.ForKey(_file, node.Line, key, "unsupported tag");
        }

        Require(YamlTags.IsAllowed(node.Tag), node, key, "unsupported tag");
        Require(node.Kind != YamlNodeKind.Scalar || node.Tag != YamlTags.Null || YamlTags.IsNullSpelling(node.Value), node, key, "null tag with a value");
    }

    /// <summary>
    /// Throws an error for the node unless the condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="node">The node the rule is about</param>
    /// <param name="key">The dotted key path reported with the error</param>
    /// <param name="reason">What is wrong</param>
    private void Require(bool condition, YamlNode node, string key, string reason)
    {
        if (condition)
        {
            return;
        }

        throw ConfigException.ForKey(_file, node.Line, key, reason);
    }

    /// <summary>
    /// Walks the entries of a mapping, which fills the properties of <paramref name="target"/>.
    /// </summary>
    /// <param name="prefix">The dotted path of the section; empty at the top level</param>
    /// <param name="mapping">The mapping node</param>
    /// <param name="target">The object the section fills</param>
    private void WalkMapping(string prefix, YamlNode mapping, object target)
    {
        var properties = OptionProperties(target.GetType());
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index + 1 < mapping.Content.Count; index += 2)
        {
            var key = mapping.Content[index];
            var value = mapping.Content[index + 1];

            CheckNode(prefix, key);
            Require(key.Kind == YamlNodeKind.Scalar, key, prefix, "key must be a string");

            if (properties.TryGetValue(key.Value, out var property))
            {
                WalkEntry(prefix, key, value, target, property, seen);
            }
            else
            {
                throw UnknownKey(prefix, key);
            }
        }
    }

    /// <summary>
    /// Handles one entry whose key names a known property; a key may appear once.
    /// </summary>
    /// <param name="prefix">The dotted path of the section</param>
    /// <param name="key">The key node</param>
    /// <param name="value">The value node</param>
    /// <param name="target">The object that owns the property</param>
    /// <param name="property">The property the key names</param>
    /// <param name="seen">The keys of the section seen so far</param>
    private void WalkEntry(string prefix, YamlNode key, YamlNode value, object target, PropertyInfo property, HashSet<string> seen)
    {
        var path = JoinPath(prefix, key.Value);

        Require(seen.Add(key.Value), key, path, "duplicate key");
        WalkField(path, key, value, target, property);
    }

    /// <summary>
    /// Handles the value of a known key.
    /// </summary>
    /// <param name="path">The dotted path of the key</param>
    /// <param name="key">The key node</param>
    /// <param name="value">The value node</param>
    /// <param name="target">The object that owns the property</param>
    /// <param name="property">The property the key names</param>
    private void WalkField(string path, YamlNode key, YamlNode value, object target, PropertyInfo property)
    {
        CheckNode(path, value);

        if (property.PropertyType == typeof(string))
        {
            SetString(path, key, value, target, property);

            return;
        }

        if (value.IsNull)
        {
            return;
        }

        if (value.Kind != YamlNodeKind.Mapping)
        {
            throw ConfigException.ForKey(_file, key.Line, path, "must be a section (a mapping of keys)");
        }

        WalkMapping(path, value, property.GetValue(target)!);
    }

    /// <summary>
    /// Stores the value of a string option.
    /// </summary>
    /// <param name="path">The dotted path of the key</param>
    /// <param name="key">The key node</param>
    /// <param name="value">The value node</param>
    /// <param name="target">The object that owns the property</param>
    /// <param name="property">The property</param>
    private void SetString(string path, YamlNode key, YamlNode value, object target, PropertyInfo property)
    {
        if (value.IsNull)
        {
            throw ConfigException.ForKey(_file, key.Line, path, "has no value");
        }

        Require(value.Kind == YamlNodeKind.Scalar, key, path, "invalid value, want a string");
        Require(IsCleanString(value.Value), key, path, "must not contain control, format or line-separating characters");

        property.SetValue(target, value.Value);
        _lines[path] = key.Line;
    }

    /// <summary>
    /// Reports an unknown key. The name is shown only when it is safe to show.
    /// </summary>
    /// <param name="prefix">The dotted path of the section</param>
    /// <param name="key">The key node</param>
    /// <returns>The error</returns>
    private ConfigException UnknownKey(string prefix, YamlNode key)
    {
        var lower = key.Value.ToLowerInvariant();
        var hint = _secretWords.Any(word => lower.Contains(word, StringComparison.Ordinal)) ? SecretHint : string.Empty;

        if (IsSafeName(key.Value))
        {
            return ConfigException.ForKey(_file, key.Line, JoinPath(prefix, key.Value), $"unknown key{hint}");
        }

        return ConfigException.ForKey(_file, key.Line, prefix, $"unknown key (name not shown: only 1 to 31 characters of [A-Za-z0-9_-] are shown){hint}");
    }

    #endregion // Methods
}