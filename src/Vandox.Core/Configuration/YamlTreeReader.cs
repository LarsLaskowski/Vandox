using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Vandox.Core.Configuration;

/// <summary>
/// Reads the single document of a YAML text into a tree of <see cref="YamlNode"/>. It uses the event parser, so an
/// alias is never expanded, the tree depth is bounded, and no parser message (which can quote the document) is passed on.
/// </summary>
internal static class YamlTreeReader
{
    #region Constants

    private const int MaxDepth = 64;
    private const string ParserReason = "not valid YAML (syntax error or alias to an undefined anchor)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the single document in <paramref name="text"/>.
    /// </summary>
    /// <param name="file">The path of the file, for error messages</param>
    /// <param name="text">The YAML text</param>
    /// <returns>The root node, or <c>null</c> when the text holds no document</returns>
    /// <exception cref="ConfigException">The text is not valid YAML, holds a second document or nests too deeply</exception>
    internal static YamlNode? Read(string file, string text)
    {
        try
        {
            var parser = new Parser(new StringReader(text));

            parser.Consume<StreamStart>();

            if (parser.TryConsume<DocumentStart>(out _))
            {
                var root = ReadNode(file, parser, 1);

                parser.Consume<DocumentEnd>();

                if (parser.TryConsume<DocumentStart>(out var second))
                {
                    throw ConfigException.ForKey(file, (int)second.Start.Line, string.Empty, "a second YAML document is not supported");
                }

                return root;
            }

            return null;
        }
        catch (YamlException exception)
        {
            throw ConfigException.ForKey(file, (int)exception.Start.Line, string.Empty, ParserReason);
        }
    }

    /// <summary>
    /// Reads the node that starts at the current event.
    /// </summary>
    /// <param name="file">The path of the file, for error messages</param>
    /// <param name="parser">The parser</param>
    /// <param name="depth">The nesting depth of the node</param>
    /// <returns>The node</returns>
    private static YamlNode ReadNode(string file, Parser parser, int depth)
    {
        if (depth > MaxDepth)
        {
            throw ConfigException.ForKey(file, (int)parser.Current!.Start.Line, string.Empty, "nesting is too deep");
        }

        var node = new YamlNode
                   {
                       Line = (int)parser.Current!.Start.Line
                   };

        switch (parser.Current)
        {
            case AnchorAlias:
                {
                    parser.MoveNext();
                    node.Kind = YamlNodeKind.Alias;

                    return node;
                }
            case Scalar scalar:
                {
                    node.Kind = YamlNodeKind.Scalar;
                    node.Value = scalar.Value;
                    node.IsPlain = scalar.Style == ScalarStyle.Plain;
                    node.HasAnchor = scalar.Anchor != AnchorName.Empty;
                    node.Tag = scalar.Tag.IsEmpty ? string.Empty : scalar.Tag.Value;
                    parser.MoveNext();

                    return node;
                }
            case MappingStart mapping:
                {
                    node.Kind = YamlNodeKind.Mapping;
                    node.HasAnchor = mapping.Anchor != AnchorName.Empty;
                    node.Tag = mapping.Tag.IsEmpty ? string.Empty : mapping.Tag.Value;
                    parser.MoveNext();
                    ReadContent<MappingEnd>(file, parser, node, depth);

                    return node;
                }
            default:
                {
                    var sequence = (SequenceStart)parser.Current;

                    node.Kind = YamlNodeKind.Sequence;
                    node.HasAnchor = sequence.Anchor != AnchorName.Empty;
                    node.Tag = sequence.Tag.IsEmpty ? string.Empty : sequence.Tag.Value;
                    parser.MoveNext();
                    ReadContent<SequenceEnd>(file, parser, node, depth);

                    return node;
                }
        }
    }

    /// <summary>
    /// Reads the children of a mapping or a sequence up to its end event.
    /// </summary>
    /// <typeparam name="TEnd">The type of the end event</typeparam>
    /// <param name="file">The path of the file, for error messages</param>
    /// <param name="parser">The parser</param>
    /// <param name="node">The node that receives the children</param>
    /// <param name="depth">The nesting depth of the node</param>
    private static void ReadContent<TEnd>(string file, Parser parser, YamlNode node, int depth)
        where TEnd : ParsingEvent
    {
        while (parser.Current is not TEnd)
        {
            node.Content.Add(ReadNode(file, parser, depth + 1));
        }

        parser.MoveNext();
    }

    #endregion // Methods
}