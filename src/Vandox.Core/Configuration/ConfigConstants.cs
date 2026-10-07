namespace Vandox.Core.Configuration;

/// <summary>
/// File locations, limits and environment variables of the configuration.
/// </summary>
public static class ConfigConstants
{
    #region Constants

    /// <summary>
    /// The default location of the backend configuration file.
    /// </summary>
    public const string DefaultBackendFile = "/etc/vandox/vandoxd.yaml";

    /// <summary>
    /// The largest configuration file that is read.
    /// </summary>
    public const int MaxFileBytes = 1 << 20;

    /// <summary>
    /// The largest secret value, also the largest <c>*_FILE</c> content (plus the line ending).
    /// </summary>
    public const int MaxSecretBytes = 4096;

    /// <summary>
    /// The shortest accepted agent token.
    /// </summary>
    public const int MinAgentTokenBytes = 32;

    /// <summary>
    /// The environment variable of the agent token.
    /// </summary>
    public const string EnvAgentToken = "VANDOX_AGENT_TOKEN";

    /// <summary>
    /// The environment variable of the web UI password hash.
    /// </summary>
    public const string EnvWebPasswordHash = "VANDOX_WEB_PASSWORD_HASH";

    /// <summary>
    /// The environment variable of the Telegram bot token.
    /// </summary>
    public const string EnvTelegramBotToken = "VANDOX_TELEGRAM_BOT_TOKEN";

    /// <summary>
    /// Appended to the name of a secret's variable to name the variable that holds a file path.
    /// </summary>
    public const string FileSuffix = "_FILE";

    #endregion // Constants
}