using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Vandox.Backend.Hosting;

/// <summary>
/// Opens listeners of the web UI and of the ingest API: every connection of a listener is labelled, so the pipeline serves
/// the web UI only on web connections and the ingest API only on ingest connections.
/// </summary>
public static class ListenerRoutes
{
    #region Constants

    /// <summary>
    /// The connection item that carries the label of the listener.
    /// </summary>
    public const string ItemName = "vandox.listener";

    /// <summary>
    /// The label of the web listener.
    /// </summary>
    public const string WebLabel = "web";

    /// <summary>
    /// The label of the ingest listener.
    /// </summary>
    public const string IngestLabel = "ingest";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Labels the connections of a listener as web connections.
    /// </summary>
    /// <param name="listener">The listener</param>
    public static void Web(ListenOptions listener)
    {
        Label(listener, WebLabel);
    }

    /// <summary>
    /// Labels the connections of a listener as ingest connections.
    /// </summary>
    /// <param name="listener">The listener</param>
    public static void Ingest(ListenOptions listener)
    {
        Label(listener, IngestLabel);
    }

    /// <summary>
    /// Adds a connection middleware that sets the label.
    /// </summary>
    /// <param name="listener">The listener</param>
    /// <param name="label">The label</param>
    private static void Label(ListenOptions listener, string label)
    {
        listener.Use(next => context =>
                             {
                                 context.Items[ItemName] = label;

                                 return next(context);
                             });
    }

    #endregion // Methods
}