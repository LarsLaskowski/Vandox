using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Ingest;

/// <summary>
/// The configuration the ingest endpoint needs: the token and the agent ID it is bound to. Without both, ingest is disabled.
/// </summary>
internal sealed class IngestSettings
{
    #region Constants

    /// <summary>
    /// The configuration key of the agent ID, named in the log line that reports ingest disabled.
    /// </summary>
    private const string AgentIdKey = "ingest.agent_id";

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IngestSettings"/> class.
    /// </summary>
    /// <param name="agentId">The agent ID the token is bound to</param>
    /// <param name="token">The check of the token</param>
    /// <param name="minFreeBytes">The free-space floor in bytes; 0 switches the check off</param>
    internal IngestSettings(string agentId, IngestToken token, long minFreeBytes)
    {
        AgentId = agentId;
        Token = token;
        MinFreeBytes = minFreeBytes;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the agent ID the token is bound to.
    /// </summary>
    internal string AgentId { get; }

    /// <summary>
    /// Gets the check of the token.
    /// </summary>
    internal IngestToken Token { get; }

    /// <summary>
    /// Gets the free-space floor in bytes (<c>ingest.min_free</c>); writes stop below it, 0 switches the check off.
    /// </summary>
    internal long MinFreeBytes { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the settings from the configuration and logs once what is missing when ingest is disabled.
    /// </summary>
    /// <param name="config">The configuration; the floor is read from <c>config.Ingest.GetMinFreeBytes()</c></param>
    /// <param name="logger">The logger</param>
    /// <returns>The settings; <c>null</c> when the token or <c>ingest.agent_id</c> is not set</returns>
    internal static IngestSettings? Create(BackendConfig config, ILogger logger)
    {
        var missing = new List<string>();

        if (string.IsNullOrEmpty(config.Secrets.AgentToken.Reveal()))
        {
            missing.Add(ConfigConstants.EnvAgentToken);
        }

        if (string.IsNullOrEmpty(config.Ingest.AgentId))
        {
            missing.Add(AgentIdKey);
        }

        if (missing.Count > 0)
        {
            logger.IngestDisabled(string.Join(", ", missing));

            return null;
        }

        return new IngestSettings(config.Ingest.AgentId!, new IngestToken(config.Secrets.AgentToken), config.Ingest.GetMinFreeBytes());
    }

    #endregion // Methods
}