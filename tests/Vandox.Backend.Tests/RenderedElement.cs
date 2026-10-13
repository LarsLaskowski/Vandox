using System.Text;

namespace Vandox.Backend.Tests;

/// <summary>
/// An element of the render tree of a <see cref="ComponentTestRenderer"/>, with the components below it expanded.
/// </summary>
internal sealed class RenderedElement
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RenderedElement"/> class.
    /// </summary>
    /// <param name="tag">The tag name</param>
    internal RenderedElement(string tag)
    {
        Tag = tag;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the tag name.
    /// </summary>
    internal string Tag { get; }

    /// <summary>
    /// Gets the attributes with a value, by name; event handlers are not among them.
    /// </summary>
    internal Dictionary<string, string> Attributes { get; } = [];

    /// <summary>
    /// Gets the identifiers of the event handlers, by event name such as <c>onclick</c>.
    /// </summary>
    internal Dictionary<string, ulong> Handlers { get; } = [];

    /// <summary>
    /// Gets the children: elements and strings (text, or markup content).
    /// </summary>
    internal List<object> Children { get; } = [];

    /// <summary>
    /// Gets the id of the component whose tree holds this element.
    /// </summary>
    internal int ComponentId { get; init; }

    /// <summary>
    /// Gets the text of the element and everything below it, without markup.
    /// </summary>
    internal string Text
    {
        get
        {
            var builder = new StringBuilder();

            AppendText(builder);

            return builder.ToString();
        }
    }

    /// <summary>
    /// Gets the class names of the element.
    /// </summary>
    internal string[] Classes => Attributes.TryGetValue("class", out var value) ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns this element and every element below it, depth first.
    /// </summary>
    /// <returns>The elements</returns>
    internal IEnumerable<RenderedElement> Descendants()
    {
        yield return this;

        foreach (var child in Children.OfType<RenderedElement>())
        {
            foreach (var element in child.Descendants())
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// Appends the text of the element.
    /// </summary>
    /// <param name="builder">The builder</param>
    private void AppendText(StringBuilder builder)
    {
        foreach (var child in Children)
        {
            if (child is RenderedElement element)
            {
                element.AppendText(builder);
            }
            else
            {
                builder.Append((string)child);
            }
        }
    }

    #endregion // Methods
}