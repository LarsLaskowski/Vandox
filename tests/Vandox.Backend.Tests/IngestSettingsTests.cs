using Microsoft.Extensions.Primitives;

using Vandox.Backend.Ingest;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="IngestSettings"/>
/// </summary>
[TestClass]
public class IngestSettingsTests
{
    #region Constants

    private const string Token = "settings-token-0123456789-abcdefghijklmnop";

    private const string TokenName = "VANDOX_AGENT_TOKEN";

    private const string AgentName = "ingest.agent_id";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Without the token or without the agent ID ingest is disabled and the missing names are logged once; with both it is enabled.
    /// </summary>
    /// <param name="tokenMissing">Whether the token is missing</param>
    /// <param name="agentMissing">Whether the agent ID is missing</param>
    [TestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void IngestSettingsMissingTokenOrAgentIdDisablesIngest(bool tokenMissing, bool agentMissing)
    {
        // Arrange
        var config = CreateConfig(tokenMissing, agentMissing);
        var log = new IngestLogCapture();

        // Act
        var settings = IngestSettings.Create(config, log);

        // Assert
        var lines = log.Named("ingest disabled");

        Assert.IsNull(settings, "ingest is disabled");
        Assert.HasCount(1, lines, "one line at the start");
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, lines[0].Level, "level");

        var missing = lines[0].Value("Missing") ?? string.Empty;

        Assert.AreEqual(tokenMissing, missing.Contains(TokenName, StringComparison.Ordinal), "the token is named when it is missing");
        Assert.AreEqual(agentMissing, missing.Contains(AgentName, StringComparison.Ordinal), "the agent ID is named when it is missing");
        Assert.DoesNotContain(Token, string.Join('\n', log.AllTexts()), "the token is not logged");
    }

    /// <summary>
    /// With the token and the agent ID ingest is enabled, binds the token to the agent ID and logs nothing.
    /// </summary>
    [TestMethod]
    public void IngestSettingsWithTokenAndAgentIdEnablesIngest()
    {
        // Arrange
        var config = CreateConfig(false, false);
        var log = new IngestLogCapture();

        // Act
        var settings = IngestSettings.Create(config, log);

        // Assert
        Assert.IsNotNull(settings, "ingest is enabled");
        Assert.AreEqual("agent-1", settings.AgentId, "the configured agent");
        Assert.IsTrue(settings.Token.Matches(new StringValues($"Bearer {Token}")), "the token of the configuration is accepted");
        Assert.IsFalse(settings.Token.Matches(new StringValues("Bearer other")), "another token is refused");
        Assert.IsEmpty(log.Entries, "nothing is logged");
    }

    /// <summary>
    /// Creates a configuration.
    /// </summary>
    /// <param name="tokenMissing">Whether the token is left out</param>
    /// <param name="agentMissing">Whether the agent ID is left out</param>
    /// <returns>The configuration</returns>
    private static BackendConfig CreateConfig(bool tokenMissing, bool agentMissing)
    {
        var config = new BackendConfig();

        config.Secrets.AgentToken = tokenMissing ? new Secret() : new Secret(Token);
        config.Ingest.AgentId = agentMissing ? null : "agent-1";

        return config;
    }

    #endregion // Methods
}