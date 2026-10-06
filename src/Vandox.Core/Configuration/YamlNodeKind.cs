namespace Vandox.Core.Configuration;

/// <summary>
/// The kind of a <see cref="YamlNode"/>.
/// </summary>
internal enum YamlNodeKind
{
    /// <summary>
    /// A scalar.
    /// </summary>
    Scalar,

    /// <summary>
    /// A mapping of keys to values.
    /// </summary>
    Mapping,

    /// <summary>
    /// A sequence of items.
    /// </summary>
    Sequence,

    /// <summary>
    /// An alias to an anchored node.
    /// </summary>
    Alias
}