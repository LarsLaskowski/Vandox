using Vandox.Core.Configuration;

namespace Vandox.Core.Signatures;

/// <summary>
/// One key and its value in a mapping of a catalog text.
/// </summary>
internal sealed class SignatureEntry
{
    #region Constructors

    /// <summary>
    /// Creates an entry.
    /// </summary>
    /// <param name="key">The key node</param>
    /// <param name="value">The value node</param>
    internal SignatureEntry(YamlNode key, YamlNode value)
    {
        Name = key.Kind == YamlNodeKind.Scalar ? key.Value : string.Empty;
        Line = key.Line;
        Value = value;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the key text, empty when the key is no scalar.
    /// </summary>
    internal string Name { get; }

    /// <summary>
    /// Gets the line of the key.
    /// </summary>
    internal int Line { get; }

    /// <summary>
    /// Gets the value node.
    /// </summary>
    internal YamlNode Value { get; }

    #endregion // Properties
}