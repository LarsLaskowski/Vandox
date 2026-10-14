using System.Text;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;

#pragma warning disable BL0006 // Renderer.UpdateDisplayAsync cannot be overridden without the RenderBatch type.

namespace Vandox.Backend.Tests;

/// <summary>
/// A <see cref="Renderer"/> that renders a root component without a browser and runs its lifecycle including <c>OnAfterRenderAsync</c>.
/// </summary>
internal sealed class ComponentTestRenderer : Renderer
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ComponentTestRenderer"/> class.
    /// </summary>
    /// <param name="serviceProvider">The services the components are created from</param>
    /// <param name="loggerFactory">The logger factory</param>
    /// <param name="interactive">Whether <see cref="RendererInfo"/> says that the renderer is interactive, as in a circuit; a component that queries only then runs its query</param>
    internal ComponentTestRenderer(IServiceProvider serviceProvider, ILoggerFactory loggerFactory, bool interactive = true)
        : base(serviceProvider, loggerFactory)
    {
        _rendererInfo = new RendererInfo("Server", interactive);
    }

    #endregion // Constructors

    #region Fields

    private readonly RendererInfo _rendererInfo;

    private TaskCompletionSource _rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the exceptions that components handed to the renderer, such as from <c>OnAfterRenderAsync</c>.
    /// </summary>
    internal List<Exception> Exceptions { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the elements of the tree of a component, depth first, with the child components expanded.
    /// </summary>
    /// <param name="componentId">The id of the component</param>
    /// <returns>The top-level elements; use <see cref="RenderedElement.Descendants"/> for all</returns>
    internal IReadOnlyList<RenderedElement> Tree(int componentId)
    {
        var roots = new List<object>();

        ReadComponent(componentId, roots);

        return roots.OfType<RenderedElement>().ToArray();
    }

    /// <summary>
    /// Returns every element of the tree of a component, depth first.
    /// </summary>
    /// <param name="componentId">The id of the component</param>
    /// <returns>The elements</returns>
    internal IReadOnlyList<RenderedElement> Elements(int componentId)
    {
        return Tree(componentId).SelectMany(element => element.Descendants()).ToArray();
    }

    /// <summary>
    /// Writes the tree of a component as HTML, with text and attribute values encoded like the framework does.
    /// </summary>
    /// <param name="componentId">The id of the component</param>
    /// <returns>The markup</returns>
    internal string ToHtml(int componentId)
    {
        var roots = new List<object>();

        ReadComponent(componentId, roots);

        var builder = new StringBuilder();

        foreach (var node in roots)
        {
            Write(builder, node);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Waits until a condition holds, checking it on the dispatcher after each render; a condition that never holds ends the wait after 10 seconds.
    /// </summary>
    /// <param name="condition">The condition</param>
    /// <returns>A task that completes when the condition holds</returns>
    internal async Task WaitUntilAsync(Func<bool> condition)
    {
        while (true)
        {
            var next = Volatile.Read(ref _rendered).Task;

            if (await Dispatcher.InvokeAsync(condition))
            {
                return;
            }

            await next.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Clicks an element: runs its <c>onclick</c> handler.
    /// </summary>
    /// <param name="element">The element</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task ClickAsync(RenderedElement element)
    {
        return DispatchAsync(element, "onclick", new MouseEventArgs(), null);
    }

    /// <summary>
    /// Submits a form: runs its <c>onsubmit</c> handler.
    /// </summary>
    /// <param name="form">The form element</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task SubmitAsync(RenderedElement form)
    {
        return DispatchAsync(form, "onsubmit", new EventArgs(), null);
    }

    /// <summary>
    /// Enters a value in an input or select: runs its <c>onchange</c> handler, or else its <c>oninput</c> handler, with the value.
    /// </summary>
    /// <param name="element">The element</param>
    /// <param name="value">The value</param>
    /// <returns>A task that completes when the handler is done</returns>
    internal Task ChangeAsync(RenderedElement element, string value)
    {
        return DispatchAsync(element,
                             element.Handlers.ContainsKey("onchange") ? "onchange" : "oninput",
                             new ChangeEventArgs
                             {
                                 Value = value
                             },
                             value);
    }

    /// <summary>
    /// Creates a root component and renders it.
    /// </summary>
    /// <typeparam name="TComponent">The type of the component</typeparam>
    /// <param name="parameters">The parameters</param>
    /// <returns>A task that returns the id of the root component</returns>
    internal Task<int> AddAsync<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        return Dispatcher.InvokeAsync(async () =>
                                      {
                                          var component = InstantiateComponent(typeof(TComponent));
                                          var id = AssignRootComponentId(component);

                                          await RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters));

                                          return id;
                                      });
    }

    /// <summary>
    /// Disposes the renderer and with it the root components.
    /// </summary>
    /// <returns>A task that completes when the components are disposed</returns>
    internal Task DisposeAllAsync()
    {
        return Dispatcher.InvokeAsync(async () => await DisposeAsync());
    }

    /// <summary>
    /// Sets new parameters on a root component and renders it again.
    /// </summary>
    /// <param name="id">The id of the root component</param>
    /// <param name="parameters">The parameters</param>
    /// <returns>A task that completes when the component is rendered</returns>
    internal Task UpdateAsync(int id, IDictionary<string, object?> parameters)
    {
        return Dispatcher.InvokeAsync(() => RenderRootComponentAsync(id, ParameterView.FromDictionary(parameters)));
    }

    /// <summary>
    /// Reads an attribute frame into an element.
    /// </summary>
    /// <param name="element">The element</param>
    /// <param name="frame">The attribute frame</param>
    private static void ReadAttribute(RenderedElement element, RenderTreeFrame frame)
    {
        if (frame.AttributeEventHandlerId != 0)
        {
            element.Handlers[frame.AttributeName] = frame.AttributeEventHandlerId;

            return;
        }

        if (frame.AttributeValue is null or false or Delegate)
        {
            return;
        }

        element.Attributes[frame.AttributeName] = frame.AttributeValue is true ? string.Empty : Convert.ToString(frame.AttributeValue, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// Writes a node as HTML.
    /// </summary>
    /// <param name="builder">The builder</param>
    /// <param name="node">An element, or text</param>
    private static void Write(StringBuilder builder, object node)
    {
        if (node is not RenderedElement element)
        {
            builder.Append(HtmlEncoder.Default.Encode((string)node));

            return;
        }

        builder.Append('<').Append(element.Tag);

        foreach (var (name, value) in element.Attributes)
        {
            builder.Append(' ').Append(name).Append("=\"").Append(HtmlEncoder.Default.Encode(value)).Append('"');
        }

        builder.Append('>');

        foreach (var child in element.Children)
        {
            Write(builder, child);
        }

        builder.Append("</").Append(element.Tag).Append('>');
    }

    /// <summary>
    /// Runs an event handler of an element on the dispatcher of the renderer.
    /// </summary>
    /// <param name="element">The element</param>
    /// <param name="eventName">The name of the event</param>
    /// <param name="arguments">The event arguments</param>
    /// <param name="fieldValue">The new value of the field, or <c>null</c></param>
    /// <returns>A task that completes when the handler is done</returns>
    private Task DispatchAsync(RenderedElement element, string eventName, EventArgs arguments, string? fieldValue)
    {
        if (element.Handlers.TryGetValue(eventName, out var handlerId))
        {
            var info = fieldValue is null
                           ? null
                           : new EventFieldInfo
                             {
                                 ComponentId = element.ComponentId,
                                 FieldValue = fieldValue
                             };

            return Dispatcher.InvokeAsync(() => DispatchEventAsync(handlerId, info, arguments));
        }

        throw new InvalidOperationException($"The element <{element.Tag}> has no {eventName} handler.");
    }

    /// <summary>
    /// Reads the current frames of a component into nodes.
    /// </summary>
    /// <param name="componentId">The id of the component</param>
    /// <param name="nodes">Receives the nodes</param>
    private void ReadComponent(int componentId, List<object> nodes)
    {
        var frames = GetCurrentRenderTreeFrames(componentId);

        ReadFrames(componentId, frames.Array, 0, frames.Count, nodes);
    }

    /// <summary>
    /// Reads a range of frames into nodes.
    /// </summary>
    /// <param name="componentId">The id of the component that owns the frames</param>
    /// <param name="frames">The frames</param>
    /// <param name="start">The first frame</param>
    /// <param name="end">The frame after the last one</param>
    /// <param name="nodes">Receives the nodes</param>
    private void ReadFrames(int componentId, RenderTreeFrame[] frames, int start, int end, List<object> nodes)
    {
        var index = start;

        while (index < end)
        {
            var frame = frames[index];

            switch (frame.FrameType)
            {
                case RenderTreeFrameType.Element:
                    {
                        var element = new RenderedElement(frame.ElementName)
                                      {
                                          ComponentId = componentId
                                      };
                        var childStart = index + 1;
                        var last = index + frame.ElementSubtreeLength;

                        while (childStart < last && frames[childStart].FrameType == RenderTreeFrameType.Attribute)
                        {
                            ReadAttribute(element, frames[childStart]);
                            childStart++;
                        }

                        ReadFrames(componentId, frames, childStart, last, element.Children);

                        nodes.Add(element);
                        index = last;
                    }
                    break;

                case RenderTreeFrameType.Text:
                    {
                        nodes.Add(frame.TextContent);
                        index++;
                    }
                    break;

                case RenderTreeFrameType.Markup:
                    {
                        nodes.Add(frame.MarkupContent);
                        index++;
                    }
                    break;

                case RenderTreeFrameType.Component:
                    {
                        ReadComponent(frame.ComponentId, nodes);
                        index += frame.ComponentSubtreeLength;
                    }
                    break;

                case RenderTreeFrameType.Region:
                    {
                        ReadFrames(componentId, frames, index + 1, index + frame.RegionSubtreeLength, nodes);
                        index += frame.RegionSubtreeLength;
                    }
                    break;

                default:
                    {
                        index++;
                    }
                    break;
            }
        }
    }

    #endregion // Methods

    #region Renderer

    /// <inheritdoc />
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <inheritdoc />
    protected override RendererInfo RendererInfo => _rendererInfo;

    /// <inheritdoc />
    protected override void HandleException(Exception exception)
    {
        Exceptions.Add(exception);
    }

    /// <inheritdoc />
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        Interlocked.Exchange(ref _rendered, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

        return Task.CompletedTask;
    }

    #endregion // Renderer
}