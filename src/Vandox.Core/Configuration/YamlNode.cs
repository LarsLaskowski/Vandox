namespace Vandox.Core.Configuration;

/// <summary>
/// A node of the YAML tree the strict decoder walks: a scalar, a mapping or a sequence.
/// </summary>
internal sealed class YamlNode
{
    #region Properties

    /// <summary>
    /// Gets or sets the kind of the node.
    /// </summary>
    internal YamlNodeKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the 1-based line of the node.
    /// </summary>
    internal int Line { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the node has an anchor.
    /// </summary>
    internal bool HasAnchor { get; set; }

    /// <summary>
    /// Gets or sets the explicit tag of the node; empty when there is none.
    /// </summary>
    internal string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text of a scalar.
    /// </summary>
    internal string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether a scalar is written without quotes.
    /// </summary>
    internal bool IsPlain { get; set; }

    /// <summary>
    /// Gets the entries of a mapping (key, value, key, value, ...) or the items of a sequence.
    /// </summary>
    internal List<YamlNode> Content { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the node is a null scalar: a plain null spelling, or the explicit null tag.
    /// </summary>
    internal bool IsNull
    {
        get
        {
            if (Kind != YamlNodeKind.Scalar)
            {
                return false;
            }

            return Tag == YamlTags.Null || (Tag.Length == 0 && IsPlain && YamlTags.IsNullSpelling(Value));
        }
    }

    #endregion // Properties
}