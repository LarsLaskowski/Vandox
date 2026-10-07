using Microsoft.AspNetCore.Server.Kestrel.Core;

using Vandox.Core.LogParsing;

namespace Vandox.Backend;

/// <summary>
/// Lets a caller (a test) replace the listeners of the service and the log parsers of the import, and learn when the service is serving. A default instance changes
/// nothing.
/// </summary>
public sealed class ServeHooks
{
    #region Properties

    /// <summary>
    /// Gets or sets an action that opens the listeners instead of the configured addresses. It must label one listener with
    /// <see cref="ListenerRoutes.Web"/> and one with <see cref="ListenerRoutes.Ingest"/>.
    /// </summary>
    public Action<KestrelServerOptions>? Listen { get; set; }

    /// <summary>
    /// Gets or sets an action that is called with the addresses of the listeners, in the order they were opened (web, then ingest), once the service serves.
    /// </summary>
    public Action<IReadOnlyList<string>>? Started { get; set; }

    /// <summary>
    /// Gets or sets the log parsers of <c>vandoxd import</c>, in priority order; none when <c>null</c>.
    /// </summary>
    public IReadOnlyList<ILogParser>? Parsers { get; set; }

    /// <summary>
    /// Gets or sets the clock used for log lines and the import; the system clock when <c>null</c>.
    /// </summary>
    public TimeProvider? Clock { get; set; }

    #endregion // Properties
}