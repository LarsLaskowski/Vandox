namespace Vandox.Backend.Cli;

/// <summary>
/// What the command line asks vandoxd to do.
/// </summary>
public sealed class CommandOptions
{
    #region Properties

    /// <summary>
    /// Gets or sets the path of the configuration file.
    /// </summary>
    public string ConfigPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to probe the health endpoint of the running service.
    /// </summary>
    public bool Healthcheck { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to print the version.
    /// </summary>
    public bool Version { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to print the usage.
    /// </summary>
    public bool Help { get; set; }

    /// <summary>
    /// Gets or sets the path to import; <c>null</c> unless the sub-command <c>import</c> was given.
    /// </summary>
    public string? ImportPath { get; set; }

    /// <summary>
    /// Gets or sets what is wrong with the command line; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets which usage text belongs to the error: <c>import</c> for the sub-command, empty for the main command.
    /// </summary>
    public string UsageFor { get; set; } = string.Empty;

    #endregion // Properties
}