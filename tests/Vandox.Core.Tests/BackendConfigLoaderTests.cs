using Vandox.Core.Configuration;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BackendConfigLoader"/> and the strict YAML decoding behind it
/// </summary>
[TestClass]
public class BackendConfigLoaderTests
{
    #region Constants

    private const string Secret = "s3cr3t-value-that-must-never-leak";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The example file of the repository loads with the defaults, sets every option explicitly and no secret.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderLoadsRepositoryExample()
    {
        // Arrange
        var path = RepositoryFiles.Path("deploy/backend/vandoxd.yaml");
        var probe = new BackendConfig();

        // Act
        var config = BackendConfigLoader.Load(path, []);
        var lines = StrictYamlDecoder.Decode(path, File.ReadAllText(path), probe);

        // Assert
        Assert.AreEqual(":8080", config.Web.Listen, "web.listen");
        Assert.AreEqual(":8081", config.Ingest.Listen, "ingest.listen");
        Assert.AreEqual("/data", config.Storage.Directory, "storage.directory");
        Assert.AreEqual("info", config.Log.Level, "log.level");
        Assert.IsFalse(config.Secrets.AgentToken.IsSet || config.Secrets.WebPasswordHash.IsSet || config.Secrets.TelegramBotToken.IsSet, "no secret is set");

        foreach (var key in BackendConfig.Keys())
        {
            Assert.IsGreaterThan(0, lines.GetValueOrDefault(key), $"the example sets {key} explicitly");
        }
    }

    /// <summary>
    /// Documents without options keep the defaults.
    /// </summary>
    /// <param name="document">The document</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("# nothing\n")]
    [DataRow("---\n")]
    [DataRow("~\n")]
    [DataRow("storage:\n")]
    [DataRow("web:\n  # listen: :1\nlog:\n  # level: debug\n")]
    public void BackendConfigLoaderKeepsDefaultsForEmptyDocuments(string document)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", document);

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual(":8080", config.Web.Listen, "web.listen");
        Assert.AreEqual("info", config.Log.Level, "log.level");
    }

    /// <summary>
    /// Every option can be set.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsAllOptions()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", "web:\n  listen: 127.0.0.1:9000\ningest:\n  listen: \"[::1]:9001\"\nstorage:\n  directory: /var/lib/vandox\nlog:\n  level: debug\n");

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual("127.0.0.1:9000", config.Web.Listen, "web.listen");
        Assert.AreEqual("[::1]:9001", config.Ingest.Listen, "ingest.listen");
        Assert.AreEqual("/var/lib/vandox", config.Storage.Directory, "storage.directory");
        Assert.AreEqual("debug", config.Log.Level, "log.level");
    }

    /// <summary>
    /// A bad document is refused with a message that names file, line and key and never echoes the document.
    /// </summary>
    /// <param name="document">The document</param>
    /// <param name="line">The expected line</param>
    /// <param name="key">The expected key</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("webb:\n  listen: :1\n", 1, "webb", "unknown key")]
    [DataRow("web:\n  listn: :1\n", 2, "web.listn", "unknown key")]
    [DataRow("Log:\n  level: info\n", 1, "Log", "unknown key")]
    [DataRow("web:\n  password_hash: x\n", 2, "web.password_hash", "unknown key; secrets are read only from environment variables, never from this file")]
    [DataRow("token: x\n", 1, "token", "unknown key; secrets are read only from environment variables, never from this file")]
    [DataRow("\"a.b.c.d.e.f.g.h.i.j.k.l.m.n.o.p.q.r.s.t.u.v.w.x.y.z\": 1\n", 1, "", "unknown key (name not shown: only 1 to 31 characters of [A-Za-z0-9_-] are shown)")]
    [DataRow("\"x\\u202Ey\": 1\n", 1, "", "unknown key (name not shown: only 1 to 31 characters of [A-Za-z0-9_-] are shown)")]
    [DataRow("web:\n  listen: :1\n  listen: :2\n", 3, "web.listen", "duplicate key")]
    [DataRow("log: {level: info, level: debug}\n", 1, "log.level", "duplicate key")]
    [DataRow("web: x\n", 1, "web", "must be a section (a mapping of keys)")]
    [DataRow("web:\n  listen:\n", 2, "web.listen", "has no value")]
    [DataRow("web:\n  listen: ~\n", 2, "web.listen", "has no value")]
    [DataRow("web:\n  listen: [a]\n", 2, "web.listen", "invalid value, want a string")]
    [DataRow("web:\n  listen: {a: b}\n", 2, "web.listen", "invalid value, want a string")]
    [DataRow("log:\n  level: \"in\\nfo\"\n", 2, "log.level", "must not contain control, format or line-separating characters")]
    [DataRow("- a\n", 1, "", "top level must be a mapping")]
    [DataRow("a: 1\n---\nb: 2\n", 2, "", "a second YAML document is not supported")]
    [DataRow("web: &a\n  listen: :1\n", 1, "web", "anchors and aliases are not supported")]
    [DataRow("log:\n  level: *a\n", 2, "log.level", "anchors and aliases are not supported")]
    [DataRow("log:\n  level: !custom info\n", 2, "log.level", "unsupported tag")]
    [DataRow("log:\n  level: !!binary aW5mbw==\n", 2, "log.level", "unsupported tag")]
    [DataRow("log:\n  level: !!null info\n", 2, "log.level", "null tag with a value")]
    [DataRow("<<: x\n", 1, "", "unsupported tag")]
    [DataRow("[[1]: 1\n", 2, "", "not valid YAML (syntax error or alias to an undefined anchor)")]
    [DataRow("? [a]\n: b\n", 1, "", "key must be a string")]
    public void BackendConfigLoaderRefusesBadDocument(string document, int line, string key, string reason)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", document);
        var position = line > 0 ? $":{line}" : string.Empty;
        var name = key.Length > 0 ? $"{key}: " : string.Empty;

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "bad document");

        // Assert
        Assert.AreEqual($"config: {path}{position}: {name}{reason}", exception.Message, "message");
    }

    /// <summary>
    /// Invalid option values are refused at the line of their key.
    /// </summary>
    /// <param name="key">The option</param>
    /// <param name="value">The raw YAML value</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("web.listen", "8080", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\":0\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\":65536\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\":+80\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\"localhost:80\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\"::1:80\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\"[[::1]:80]:18080\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\"[::1%eth0]:80\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("web.listen", "\"1.2.3.4:\"", "must be [host]:port with an empty host or an IP address and a port of 1 to 65535")]
    [DataRow("ingest.listen", "\":9999\"", "must not use the same port as web.listen")]
    [DataRow("storage.directory", "data", "must be an absolute, clean path (no trailing slash, no . or .. elements)")]
    [DataRow("storage.directory", "/data/", "must be an absolute, clean path (no trailing slash, no . or .. elements)")]
    [DataRow("storage.directory", "/data/../x", "must be an absolute, clean path (no trailing slash, no . or .. elements)")]
    [DataRow("storage.directory", "/a//b", "must be an absolute, clean path (no trailing slash, no . or .. elements)")]
    [DataRow("log.level", "verbose", "must be one of debug, info, warn, error")]
    public void BackendConfigLoaderRefusesInvalidOption(string key, string value, string reason)
    {
        // Arrange
        var lines = new Dictionary<string, int>
                    {
                        ["web.listen"] = 2,
                        ["ingest.listen"] = 4,
                        ["storage.directory"] = 6,
                        ["log.level"] = 8
                    };
        var values = new Dictionary<string, string>
                     {
                         ["web.listen"] = "\":9999\"",
                         ["ingest.listen"] = "\":9998\"",
                         ["storage.directory"] = "/data",
                         ["log.level"] = "info"
                     };

        values[key] = value;

        using var directory = new TempDirectory();
        var document = $"web:\n  listen: {values["web.listen"]}\ningest:\n  listen: {values["ingest.listen"]}\nstorage:\n  directory: {values["storage.directory"]}\nlog:\n  level: {values["log.level"]}\n";
        var path = directory.Write("vandoxd.yaml", document);

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "invalid option");

        // Assert
        Assert.AreEqual($"config: {path}:{lines[key]}: {key}: {reason}", exception.Message, "message");
    }

    /// <summary>
    /// The port of a listen address is parsed, or 0 is returned.
    /// </summary>
    /// <param name="value">The address</param>
    /// <param name="port">The expected port</param>
    [TestMethod]
    [DataRow(":8080", 8080)]
    [DataRow("0.0.0.0:1", 1)]
    [DataRow("[::1]:65535", 65535)]
    [DataRow("[::]:80", 80)]
    [DataRow("8080", 0)]
    [DataRow("[::1:80", 0)]
    [DataRow("1.2.3:80", 0)]
    [DataRow(":123456", 0)]
    [DataRow(":", 0)]
    public void BackendConfigLoaderParseListenPortReturnsPort(string value, int port)
    {
        // Act
        var parsed = BackendConfigLoader.ParseListenPort(value);

        // Assert
        Assert.AreEqual(port, parsed, "port");
    }

    /// <summary>
    /// A missing file, a directory, a device and an oversized file are refused with a message that holds only the path.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderRefusesUnreadableFiles()
    {
        // Arrange
        using var directory = new TempDirectory();
        var large = directory.Write("large.yaml", "# " + new string('x', ConfigConstants.MaxFileBytes));
        var missing = Path.Combine(directory.Path, "missing.yaml");

        // Act
        var absent = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(missing, []), "missing file");
        var folder = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(directory.Path, []), "directory");
        var device = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load("/dev/null", []), "device");
        var tooLarge = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(large, []), "large file");

        // Assert
        Assert.AreEqual($"config: {missing}: no such file or directory", absent.Message, "missing");
        Assert.AreEqual($"config: {directory.Path}: not a regular file", folder.Message, "directory");
        Assert.AreEqual("config: /dev/null: not a regular file", device.Message, "device");
        Assert.AreEqual($"config: {large}: file is larger than {ConfigConstants.MaxFileBytes} bytes", tooLarge.Message, "large");
    }

    /// <summary>
    /// A document nested too deeply is refused instead of exhausting the stack.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderRefusesDeepNesting()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"x: {new string('[', 5000)}{new string(']', 5000)}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "deep document");

        // Assert
        Assert.EndsWith("nesting is too deep", exception.Message, "reason");
    }

    /// <summary>
    /// Secrets are read from the environment and stay hidden in every format.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsSecretsFromEnvironment()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", string.Empty);
        var token = new string('a', ConfigConstants.MinAgentTokenBytes);
        var hashFile = directory.Write("hash", "$argon2id$v=19$m=65536\r\n");
        var environment = new Dictionary<string, string>
                          {
                              [ConfigConstants.EnvAgentToken] = token,
                              [ConfigConstants.EnvWebPasswordHash + ConfigConstants.FileSuffix] = hashFile,
                              ["PATH"] = "/usr/bin"
                          };

        // Act
        var config = BackendConfigLoader.Load(path, environment);
        var json = System.Text.Json.JsonSerializer.Serialize(config.Secrets);

        // Assert
        Assert.AreEqual(token, config.Secrets.AgentToken.Reveal(), "agent token");
        Assert.AreEqual("$argon2id$v=19$m=65536", config.Secrets.WebPasswordHash.Reveal(), "password hash from the file without the line ending");
        Assert.IsFalse(config.Secrets.TelegramBotToken.IsSet, "unset secret");
        Assert.AreEqual(Core.Configuration.Secret.RedactedText, config.Secrets.AgentToken.ToString(), "ToString");
        Assert.AreEqual("[redacted]", $"{config.Secrets.AgentToken}", "interpolation");
        Assert.DoesNotContain(token, json, "serialized secrets");
        Assert.Contains(Core.Configuration.Secret.RedactedText, json, "serialized secrets show the placeholder");
    }

    /// <summary>
    /// A bad secret is refused with a message that never shows the value or a file path.
    /// </summary>
    /// <param name="variable">The variable</param>
    /// <param name="value">The value</param>
    /// <param name="message">The expected message</param>
    [TestMethod]
    [DataRow("VANDOX_AGENT_TOKEN", "short", "config: VANDOX_AGENT_TOKEN: must be at least 32 characters")]
    [DataRow("VANDOX_WEB_PASSWORD_HASH", "", "config: VANDOX_WEB_PASSWORD_HASH: is empty")]
    [DataRow("VANDOX_WEB_PASSWORD_HASH", "has space", "config: VANDOX_WEB_PASSWORD_HASH: must consist of printable ASCII characters without spaces")]
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN", "äbc", "config: VANDOX_TELEGRAM_BOT_TOKEN: must consist of printable ASCII characters without spaces")]
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN_FILE", "relative/path", "config: VANDOX_TELEGRAM_BOT_TOKEN_FILE: must be an absolute path")]
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN_FILE", "/dev/null", "config: VANDOX_TELEGRAM_BOT_TOKEN_FILE: must name a regular file")]
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN_FILE", "/nonexistent/secret", "config: VANDOX_TELEGRAM_BOT_TOKEN_FILE: cannot read the file: no such file or directory")]
    [DataRow("VANDOX_OTHER", "x", "config: VANDOX_OTHER: unknown VANDOX_ variable")]
    [DataRow("vandox_agent_token", "x", "config: vandox_agent_token: unknown VANDOX_ variable")]
    [DataRow("VANDOX_A-B", "x", "config: unknown VANDOX_ variable (name not shown: only 1 to 64 characters of [A-Za-z0-9_] are shown)")]
    public void BackendConfigLoaderRefusesBadSecret(string variable, string value, string message)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", string.Empty);

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path,
                                                                                             new Dictionary<string, string>
                                                                                             {
                                                                                                 [variable] = value
                                                                                             }),
                                                              "bad secret");

        // Assert
        Assert.AreEqual(message, exception.Message, "message");
    }

    /// <summary>
    /// A secret given directly and through a file, a duplicate variable and an oversized secret file are refused.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderRefusesConflictingAndOversizedSecrets()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", string.Empty);
        var file = directory.Write("secret", "value");
        var large = directory.Write("large", new string('a', ConfigConstants.MaxSecretBytes + 3));
        var both = new Dictionary<string, string>
                   {
                       [ConfigConstants.EnvWebPasswordHash] = "x",
                       [ConfigConstants.EnvWebPasswordHash + ConfigConstants.FileSuffix] = file
                   };
        var duplicate = new List<KeyValuePair<string, string>>
                        {
                            new(ConfigConstants.EnvWebPasswordHash, "x"),
                            new(ConfigConstants.EnvWebPasswordHash, "y")
                        };
        var oversized = new Dictionary<string, string>
                        {
                            [ConfigConstants.EnvWebPasswordHash + ConfigConstants.FileSuffix] = large
                        };
        var longValue = new Dictionary<string, string>
                        {
                            [ConfigConstants.EnvWebPasswordHash] = new string('a', ConfigConstants.MaxSecretBytes + 1)
                        };

        // Act
        var conflict = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, both), "both forms");
        var twice = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, duplicate), "duplicate variable");
        var tooLarge = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, oversized), "large secret file");
        var tooLong = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, longValue), "long secret");

        // Assert
        Assert.AreEqual("config: VANDOX_WEB_PASSWORD_HASH: VANDOX_WEB_PASSWORD_HASH and VANDOX_WEB_PASSWORD_HASH_FILE are both set, set only one", conflict.Message, "both forms");
        Assert.AreEqual("config: VANDOX_WEB_PASSWORD_HASH: set more than once", twice.Message, "duplicate");
        Assert.AreEqual($"config: VANDOX_WEB_PASSWORD_HASH_FILE: file is larger than {ConfigConstants.MaxSecretBytes} bytes plus the line ending", tooLarge.Message, "large file");
        Assert.AreEqual($"config: VANDOX_WEB_PASSWORD_HASH: is longer than {ConfigConstants.MaxSecretBytes} bytes", tooLong.Message, "long value");
    }

    /// <summary>
    /// A secret value never appears in an error message.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderNeverEchoesSecretValue()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"web:\n  listen: {Secret}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "secret pasted as a value");

        // Assert
        Assert.DoesNotContain(Secret, exception.Message, "the pasted value stays out of the message");
    }

    #endregion // Methods
}