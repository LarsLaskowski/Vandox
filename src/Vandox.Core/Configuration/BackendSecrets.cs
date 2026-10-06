namespace Vandox.Core.Configuration;

/// <summary>
/// The secrets of the backend; each is optional at load time.
/// </summary>
public sealed class BackendSecrets
{
    #region Properties

    /// <summary>
    /// Gets or sets the token the agents present.
    /// </summary>
    public Secret AgentToken { get; set; } = new();

    /// <summary>
    /// Gets or sets the password hash of the web UI.
    /// </summary>
    public Secret WebPasswordHash { get; set; } = new();

    /// <summary>
    /// Gets or sets the token of the Telegram bot.
    /// </summary>
    public Secret TelegramBotToken { get; set; } = new();

    #endregion // Properties
}