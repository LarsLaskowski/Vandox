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
    public WebConfig Web { get; } = new();

    /// <summary>
    /// Gets the ingest listener.
    /// </summary>
    [ConfigKey("ingest")]
    public IngestConfig Ingest { get; } = new();

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
    /// Gets the import options.
    /// </summary>
    [ConfigKey("import")]
    public ImportConfig Import { get; } = new();

    /// <summary>
    /// Gets the incident reconstruction options.
    /// </summary>
    [ConfigKey("incidents")]
    public IncidentConfig Incidents { get; } = new();

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
        return ["web.listen", "web.trusted_proxies", "ingest.listen", "storage.directory", "log.level", "import.plesk_panel_log", "import.sw_cp_server_error_log", "import.nginx_error_log", "import.apache_error_log", "import.time_zone", "incidents.window_before", "incidents.window_after"];
    }

    #endregion // Methods
}