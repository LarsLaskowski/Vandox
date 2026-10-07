namespace Vandox.Core.Configuration;

/// <summary>
/// The configuration of vandoxd.
/// </summary>
public sealed class BackendConfig
{
    #region Properties

    /// <summary>
    /// Gets the web UI listener.
    /// </summary>
    [ConfigKey("web")]
    public ListenerConfig Web { get; } = new()
                                         {
                                             Listen = ":8080"
                                         };

    /// <summary>
    /// Gets the ingest listener.
    /// </summary>
    [ConfigKey("ingest")]
    public ListenerConfig Ingest { get; } = new()
                                            {
                                                Listen = ":8081"
                                            };

    /// <summary>
    /// Gets the storage options.
    /// </summary>
    [ConfigKey("storage")]
    public StorageConfig Storage { get; } = new();

    /// <summary>
    /// Gets the logging options.
    /// </summary>
    [ConfigKey("log")]
    public LogConfig Log { get; } = new();

    /// <summary>
    /// Gets or sets the secrets of the backend.
    /// </summary>
    public BackendSecrets Secrets { get; set; } = new();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns every option key path in file order.
    /// </summary>
    /// <returns>The key paths</returns>
    public static IReadOnlyList<string> Keys()
    {
        return ["web.listen", "ingest.listen", "storage.directory", "log.level"];
    }

    #endregion // Methods
}