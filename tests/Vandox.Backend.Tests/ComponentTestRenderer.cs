using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
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
    internal ComponentTestRenderer(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
        : base(serviceProvider, loggerFactory)
    {
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the exceptions that components handed to the renderer, such as from <c>OnAfterRenderAsync</c>.
    /// </summary>
    internal List<Exception> Exceptions { get; } = [];

    #endregion // Properties

    #region Methods

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

    #endregion // Methods

    #region Renderer

    /// <inheritdoc />
    public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

    /// <inheritdoc />
    protected override void HandleException(Exception exception)
    {
        Exceptions.Add(exception);
    }

    /// <inheritdoc />
    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        return Task.CompletedTask;
    }

    #endregion // Renderer
}