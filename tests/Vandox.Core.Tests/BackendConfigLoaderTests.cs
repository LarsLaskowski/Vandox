using Vandox.Core.Configuration;
using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="BackendConfigLoader"/> and the strict YAML decoding behind it
/// </summary>
[TestClass]
public class BackendConfigLoaderTests
{
    #region Constants

    private const string Secret = "s3cr3t-value-that-must-never-leak";

    private const string AgentIdReason = "must be 1 to 64 characters of [A-Za-z0-9._-], starting with a letter or digit";

    private const string MinFreeReason = "must be a whole number of MiB or GiB from 0MiB to 1024GiB, such as 0MiB or 1GiB";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// The example file of the repository loads with the defaults, sets every option that has a default explicitly, holds the option without a default commented out and sets no secret.
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
        Assert.AreEqual("1GiB", config.Ingest.MinFree, "ingest.min_free");
        Assert.AreEqual("/data", config.Storage.Directory, "storage.directory");
        Assert.AreEqual("info", config.Log.Level, "log.level");
        Assert.IsFalse(config.Secrets.AgentToken.IsSet || config.Secrets.WebPasswordHash.IsSet || config.Secrets.TelegramBotToken.IsSet, "no secret is set");

        foreach (var key in BackendConfig.Keys().Where(key => key != "import.time_zone" && key != "ingest.agent_id"))
        {
            Assert.IsGreaterThan(0, lines.GetValueOrDefault(key), $"the example sets {key} explicitly");
        }

        Assert.AreEqual(ErrorLogPaths.Default, config.Import.GetErrorLogPaths(), "the example sets the default error log paths");
        Assert.IsNull(config.Import.TimeZone, "import.time_zone has no default and the example leaves it unset");
        Assert.AreEqual(0, lines.GetValueOrDefault("import.time_zone"), "the example does not set import.time_zone");
        Assert.IsNull(config.Ingest.AgentId, "ingest.agent_id has no default and the example leaves it unset");
        Assert.AreEqual(0, lines.GetValueOrDefault("ingest.agent_id"), "the example does not set ingest.agent_id");
        Assert.Contains("\n  # agent_id: ", File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), "the example holds ingest.agent_id commented out");
        Assert.Contains("\n  # time_zone: Europe/Berlin\n", File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), "the example holds the option commented out");
    }

    /// <summary>
    /// The time zone has no default; the key is absent, or its section is empty or holds only comments.
    /// </summary>
    /// <param name="document">The document</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("web:\n  listen: :9000\n")]
    [DataRow("import:\n")]
    [DataRow("import: ~\n")]
    [DataRow("import:\n  # time_zone: Europe/Berlin\n")]
    [DataRow("import: {}\n")]
    public void BackendConfigLoaderLeavesTimeZoneUnsetWithoutDefault(string document)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", document);

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.IsNull(config.Import.TimeZone, "import.time_zone is null");
    }

    /// <summary>
    /// Names of the IANA time zone database are accepted.
    /// </summary>
    /// <param name="zone">The zone</param>
    [TestMethod]
    [DataRow("UTC")]
    [DataRow("Etc/UTC")]
    [DataRow("Europe/Berlin")]
    [DataRow("\"Europe/Berlin\"")]
    public void BackendConfigLoaderReadsTimeZone(string zone)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"import:\n  time_zone: {zone}\n");

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual(zone.Trim('"'), config.Import.TimeZone, "import.time_zone");
    }

    /// <summary>
    /// A value that is no time zone of the database is refused at its line and never shown.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    [TestMethod]
    [DataRow("\"\"")]
    [DataRow("Europe/Nowhere")]
    [DataRow("\"+01:00\"")]
    [DataRow("W. Europe Standard Time")]
    [DataRow("europe/berlin")]
    public void BackendConfigLoaderRefusesInvalidTimeZoneWithoutShowingIt(string value)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"import:\n  time_zone: {value}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "invalid time zone");

        // Assert
        Assert.AreEqual($"config: {path}:2: import.time_zone: must be a time zone of the IANA time zone database, such as UTC or Europe/Berlin", exception.Message, "message");
    }

    /// <summary>
    /// A value of a log path option that is not an absolute, clean path of a file in a directory is refused at the line of its key with a fixed text that does not show the value.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    [TestMethod]
    [DataRow("var/log/x.log")]
    [DataRow("/error.log")]
    [DataRow("/")]
    [DataRow("/var/log/nginx/")]
    [DataRow("/var/log/../x.log")]
    [DataRow("/var/log/./x.log")]
    [DataRow("/var//log/x.log")]
    [DataRow("\"\"")]
    public void BackendConfigLoaderRefusesInvalidErrorLogPathWithoutShowingIt(string value)
    {
        // Arrange
        string[] keys = ["plesk_panel_log", "sw_cp_server_error_log", "nginx_error_log", "apache_error_log"];

        foreach (var key in keys)
        {
            using var directory = new TempDirectory();
            var path = directory.Write("vandoxd.yaml", $"import:\n  {key}: {value}\n");

            // Act
            var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), $"invalid {key}");

            // Assert
            Assert.AreEqual($"config: {path}:2: import.{key}: must be an absolute, clean path of a file in a directory (no trailing slash, no . or .. elements)", exception.Message, $"message for {key}");
        }
    }

    /// <summary>
    /// A time zone without a value is refused like every string option.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    [TestMethod]
    [DataRow("~")]
    [DataRow("")]
    public void BackendConfigLoaderRefusesTimeZoneWithoutValue(string value)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"import:\n  time_zone: {value}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "no value");

        // Assert
        Assert.AreEqual($"config: {path}:2: import.time_zone: has no value", exception.Message, "message");
    }

    /// <summary>
    /// The list of keys names the time zone after the other import options and before the incident windows.
    /// </summary>
    [TestMethod]
    public void BackendConfigKeysListTimeZone()
    {
        // Act
        var keys = BackendConfig.Keys();

        // Assert
        Assert.AreEqual("incidents.window_after", keys[^1], "the window after is the last key");
        Assert.AreEqual("incidents.window_before", keys[^2], "the window before is the second last key");
        Assert.AreEqual("import.time_zone", keys[^3], "the time zone is the last import key");
        Assert.Contains("log.level", keys, "the other keys stay");
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
    /// The option <c>web.trusted_proxies</c> is empty by default and is read as the networks it names.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsTrustedProxies()
    {
        // Arrange
        using var directory = new TempDirectory();
        var absent = directory.Write("absent.yaml", "web:\n  listen: :9000\n");
        var empty = directory.Write("empty.yaml", "web:\n  trusted_proxies: \"\"\n");
        var set = directory.Write("set.yaml", "web:\n  listen: :9000\n  trusted_proxies: \"172.18.0.1, 10.0.0.0/8\"\n");

        // Act
        var absentConfig = BackendConfigLoader.Load(absent, []);
        var emptyConfig = BackendConfigLoader.Load(empty, []);
        var setConfig = BackendConfigLoader.Load(set, []);

        // Assert
        Assert.AreEqual(string.Empty, absentConfig.Web.TrustedProxies, "empty by default");
        Assert.IsEmpty(absentConfig.Web.GetTrustedProxies(), "no network by default");
        Assert.IsEmpty(emptyConfig.Web.GetTrustedProxies(), "no network for an empty value");
        Assert.AreEqual("172.18.0.1, 10.0.0.0/8", setConfig.Web.TrustedProxies, "the value as written");
        Assert.AreEqual("172.18.0.1/32|10.0.0.0/8", string.Join('|', setConfig.Web.GetTrustedProxies().Select(network => $"{network.BaseAddress}/{network.PrefixLength}")), "networks");
    }

    /// <summary>
    /// An invalid <c>web.trusted_proxies</c> is refused at the line of its key with the fixed message, which does not contain the value.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    /// <param name="marker">The part of the value that must not appear in the message</param>
    [TestMethod]
    [DataRow("\"proxy.leak-marker\"", "leak-marker")]
    [DataRow("\"172.18.0.1/16\"", "172.18.0.1/16")]
    [DataRow("\"10.0.0.1, ,10.0.0.2\"", "10.0.0.2")]
    [DataRow("\"10.0.0.1,10.0.0.2,10.0.0.3,10.0.0.4,10.0.0.5,10.0.0.6,10.0.0.7,10.0.0.8,10.0.0.9,10.0.0.10,10.0.0.11,10.0.0.12,10.0.0.13,10.0.0.14,10.0.0.15,10.0.0.16,10.0.0.17\"", "10.0.0.17")]
    public void BackendConfigLoaderRefusesInvalidTrustedProxiesWithoutValue(string value, string marker)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"web:\n  listen: \":9999\"\n  trusted_proxies: {value}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "invalid option");

        // Assert
        Assert.AreEqual($"config: {path}:3: web.trusted_proxies: must be a comma-separated list of at most 16 IP addresses or networks, such as 172.18.0.1 or 172.18.0.0/16", exception.Message, "message");
        Assert.DoesNotContain(marker, exception.Message, "the message does not contain the value");
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
    [OSCondition(OperatingSystems.Linux)]
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
    [OSCondition(OperatingSystems.Linux)]
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
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN", "\u00E4bc", "config: VANDOX_TELEGRAM_BOT_TOKEN: must consist of printable ASCII characters without spaces")]
    [DataRow("VANDOX_TELEGRAM_BOT_TOKEN_FILE", "relative/path", "config: VANDOX_TELEGRAM_BOT_TOKEN_FILE: must be an absolute path")]
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
    /// A secret file that names a device is refused with a message that never shows the path.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void BackendConfigLoaderRefusesSecretFileThatIsADevice()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", string.Empty);
        var environment = new Dictionary<string, string>
                          {
                              ["VANDOX_TELEGRAM_BOT_TOKEN_FILE"] = "/dev/null"
                          };

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, environment), "device as secret file");

        // Assert
        Assert.AreEqual("config: VANDOX_TELEGRAM_BOT_TOKEN_FILE: must name a regular file", exception.Message, "message");
    }

    /// <summary>
    /// A secret given directly and through a file, a duplicate variable and an oversized secret file are refused.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
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

    /// <summary>
    /// Without the options the error log paths are those of a current Plesk on Ubuntu.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderDefaultsErrorLogPaths()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", string.Empty);

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual("/var/log/plesk/panel.log", config.Import.PleskPanelLog, "import.plesk_panel_log");
        Assert.AreEqual("/var/log/sw-cp-server/error_log", config.Import.SwCpServerErrorLog, "import.sw_cp_server_error_log");
        Assert.AreEqual("/var/log/nginx/error.log", config.Import.NginxErrorLog, "import.nginx_error_log");
        Assert.AreEqual("/var/log/apache2/error.log", config.Import.ApacheErrorLog, "import.apache_error_log");
        Assert.AreEqual(ErrorLogPaths.Default, config.Import.GetErrorLogPaths(), "the paths as a record");
    }

    /// <summary>
    /// The four error log paths are read from the file.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsErrorLogPaths()
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", "import:\n  plesk_panel_log: /usr/local/psa/admin/logs/panel.log\n  sw_cp_server_error_log: /srv/a/error_log\n  nginx_error_log: /srv/b/proxy.log\n  apache_error_log: /var/log/httpd/error_log\n");

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual(new ErrorLogPaths("/usr/local/psa/admin/logs/panel.log", "/srv/a/error_log", "/srv/b/proxy.log", "/var/log/httpd/error_log"), config.Import.GetErrorLogPaths(), "the configured paths");
    }

    /// <summary>
    /// A log path option without a value is refused like every string option.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    [TestMethod]
    [DataRow("~")]
    [DataRow("")]
    public void BackendConfigLoaderRefusesErrorLogPathWithoutValue(string value)
    {
        // Arrange
        string[] keys = ["plesk_panel_log", "sw_cp_server_error_log", "nginx_error_log", "apache_error_log"];

        foreach (var key in keys)
        {
            using var directory = new TempDirectory();
            var path = directory.Write("vandoxd.yaml", $"import:\n  {key}: {value}\n");

            // Act
            var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), $"no value for {key}");

            // Assert
            Assert.AreEqual($"config: {path}:2: import.{key}: has no value", exception.Message, $"message for {key}");
        }
    }

    /// <summary>
    /// The option <c>ingest.agent_id</c> has no default and is read as written.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsIngestAgentId()
    {
        // Arrange
        using var directory = new TempDirectory();
        var absent = directory.Write("absent.yaml", "ingest:\n  listen: :9000\n");
        var set = directory.Write("set.yaml", "ingest:\n  agent_id: Web-1.a_b\n");
        var longest = new string('a', 64);
        var longestPath = directory.Write("longest.yaml", $"ingest:\n  agent_id: {longest}\n");

        // Act
        var absentConfig = BackendConfigLoader.Load(absent, []);
        var setConfig = BackendConfigLoader.Load(set, []);
        var longestConfig = BackendConfigLoader.Load(longestPath, []);

        // Assert
        Assert.IsNull(absentConfig.Ingest.AgentId, "no default");
        Assert.AreEqual("Web-1.a_b", setConfig.Ingest.AgentId, "the value as written, case kept");
        Assert.AreEqual(longest, longestConfig.Ingest.AgentId, "64 characters are allowed");
    }

    /// <summary>
    /// An <c>ingest.agent_id</c> without value or against the agent ID rule is refused at the line of its key with a fixed text that does not show the value.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("~", "has no value")]
    [DataRow("", "has no value")]
    [DataRow("\"\"", AgentIdReason)]
    [DataRow("\"-web\"", AgentIdReason)]
    [DataRow("\".web\"", AgentIdReason)]
    [DataRow("\"_web\"", AgentIdReason)]
    [DataRow("\"web 1\"", AgentIdReason)]
    [DataRow("\"web/1\"", AgentIdReason)]
    [DataRow("\"web:1\"", AgentIdReason)]
    [DataRow("\"w\\u00e9b\"", AgentIdReason)]
    [DataRow("\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", AgentIdReason)]
    public void BackendConfigLoaderRefusesInvalidIngestAgentIdWithoutValue(string value, string reason)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"ingest:\n  agent_id: {value}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "invalid agent ID");

        // Assert
        Assert.AreEqual($"config: {path}:2: ingest.agent_id: {reason}", exception.Message, "message");
    }

    /// <summary>
    /// The option <c>ingest.min_free</c> is one gibibyte by default and is read in bytes.
    /// </summary>
    /// <param name="document">The configuration document</param>
    /// <param name="bytes">The expected number of bytes</param>
    [TestMethod]
    [DataRow("ingest:\n  listen: :9000\n", 1073741824L)]
    [DataRow("ingest:\n  min_free: 0MiB\n", 0L)]
    [DataRow("ingest:\n  min_free: 0GiB\n", 0L)]
    [DataRow("ingest:\n  min_free: 1MiB\n", 1048576L)]
    [DataRow("ingest:\n  min_free: 512MiB\n", 536870912L)]
    [DataRow("ingest:\n  min_free: 2GiB\n", 2147483648L)]
    [DataRow("ingest:\n  min_free: 99999MiB\n", 104856551424L)]
    [DataRow("ingest:\n  min_free: 1024GiB\n", 1099511627776L)]
    public void BackendConfigLoaderReadsIngestMinFree(string document, long bytes)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", document);

        // Act
        var config = BackendConfigLoader.Load(path, []);

        // Assert
        Assert.AreEqual(bytes, config.Ingest.GetMinFreeBytes(), "bytes");
    }

    /// <summary>
    /// An <c>ingest.min_free</c> without value or against the size rule is refused at the line of its key with a fixed text that does not show the value.
    /// </summary>
    /// <param name="value">The raw YAML value</param>
    /// <param name="reason">The expected reason</param>
    [TestMethod]
    [DataRow("~", "has no value")]
    [DataRow("", "has no value")]
    [DataRow("\"\"", MinFreeReason)]
    [DataRow("1024", MinFreeReason)]
    [DataRow("\"1 GiB\"", MinFreeReason)]
    [DataRow("1.5GiB", MinFreeReason)]
    [DataRow("1GB", MinFreeReason)]
    [DataRow("1gib", MinFreeReason)]
    [DataRow("-1GiB", MinFreeReason)]
    [DataRow("+1GiB", MinFreeReason)]
    [DataRow("01GiB", MinFreeReason)]
    [DataRow("GiB", MinFreeReason)]
    [DataRow("1025GiB", MinFreeReason)]
    [DataRow("1048577MiB", MinFreeReason)]
    [DataRow("100000MiB", MinFreeReason)]
    public void BackendConfigLoaderRefusesInvalidIngestMinFreeWithoutValue(string value, string reason)
    {
        // Arrange
        using var directory = new TempDirectory();
        var path = directory.Write("vandoxd.yaml", $"ingest:\n  min_free: {value}\n");

        // Act
        var exception = Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(path, []), "invalid minimum free space");

        // Assert
        Assert.AreEqual($"config: {path}:2: ingest.min_free: {reason}", exception.Message, "message");
    }

    /// <summary>
    /// The list of keys names the error log paths after <c>log.level</c> and before the time zone, and the incident windows last.
    /// </summary>
    [TestMethod]
    public void BackendConfigKeysListErrorLogPathsBeforeTimeZone()
    {
        // Act
        var keys = BackendConfig.Keys();

        // Assert
        Assert.AreSequenceEqual(["web.listen", "web.trusted_proxies", "ingest.listen", "ingest.agent_id", "ingest.min_free", "storage.directory", "log.level", "import.plesk_panel_log", "import.sw_cp_server_error_log", "import.nginx_error_log", "import.apache_error_log", "import.time_zone", "incidents.window_before", "incidents.window_after"], keys, "keys in order");
    }

    /// <summary>
    /// The incident windows are read from the <c>incidents</c> section, quoted or not, and keep their defaults when the section is empty.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderReadsIncidentWindows()
    {
        // Arrange
        using var directory = new TempDirectory();
        var plain = directory.Write("plain.yaml", "incidents:\n  window_before: 2h\n  window_after: 15m\n");
        var quoted = directory.Write("quoted.yaml", "incidents:\n  window_before: \"1h\"\n");
        var empty = directory.Write("empty.yaml", "incidents:\n");

        // Act
        var plainConfig = BackendConfigLoader.Load(plain, []);
        var quotedConfig = BackendConfigLoader.Load(quoted, []);
        var emptyConfig = BackendConfigLoader.Load(empty, []);

        // Assert
        Assert.AreEqual(TimeSpan.FromHours(2), plainConfig.Incidents.GetWindowBefore(), "window_before 2h");
        Assert.AreEqual(TimeSpan.FromMinutes(15), plainConfig.Incidents.GetWindowAfter(), "window_after 15m");
        Assert.AreEqual(TimeSpan.FromHours(1), quotedConfig.Incidents.GetWindowBefore(), "quoted window_before");
        Assert.AreEqual(TimeSpan.FromMinutes(30), quotedConfig.Incidents.GetWindowAfter(), "window_after keeps its default");
        Assert.AreEqual(TimeSpan.FromMinutes(60), emptyConfig.Incidents.GetWindowBefore(), "default window_before");
        Assert.AreEqual(TimeSpan.FromMinutes(30), emptyConfig.Incidents.GetWindowAfter(), "default window_after");
    }

    /// <summary>
    /// A window outside 1 minute to 24 hours, or in another form than whole minutes or hours, is refused at its line with a fixed text that does not show the value; the bounds are accepted and a null value has no value.
    /// </summary>
    [TestMethod]
    public void BackendConfigLoaderRefusesInvalidIncidentWindows()
    {
        // Arrange
        const string reason = "must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h";
        string[] invalid = ["0m", "25h", "1441m", "60", "1.5h"];
        string[] bounds = ["1m", "24h", "1440m"];
        using var directory = new TempDirectory();

        // Act and assert
        foreach (var value in invalid)
        {
            var before = directory.Write("before.yaml", $"incidents:\n  window_before: {value}\n");
            var after = directory.Write("after.yaml", $"incidents:\n  window_after: {value}\n");
            var both = directory.Write("both.yaml", $"incidents:\n  window_before: 1h\n  window_after: {value}\n");

            Assert.AreEqual($"config: {before}:2: incidents.window_before: {reason}", Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(before, []), $"window_before {value}").Message, $"message for window_before {value}");
            Assert.AreEqual($"config: {after}:2: incidents.window_after: {reason}", Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(after, []), $"window_after {value}").Message, $"message for window_after {value}");
            Assert.AreEqual($"config: {both}:3: incidents.window_after: {reason}", Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(both, []), $"window_after {value} on line 3").Message, $"message at the line of the second option for {value}");
        }

        foreach (var value in bounds)
        {
            var path = directory.Write("bounds.yaml", $"incidents:\n  window_before: {value}\n  window_after: {value}\n");
            var config = BackendConfigLoader.Load(path, []);

            Assert.IsGreaterThan(TimeSpan.Zero, config.Incidents.GetWindowBefore(), $"window_before {value} is accepted");
            Assert.AreEqual(config.Incidents.GetWindowBefore(), config.Incidents.GetWindowAfter(), $"window_after {value} is accepted");
        }

        var nothing = directory.Write("nothing.yaml", "incidents:\n  window_before: ~\n");

        Assert.AreEqual($"config: {nothing}:2: incidents.window_before: has no value", Assert.ThrowsExactly<ConfigException>(() => BackendConfigLoader.Load(nothing, []), "null value").Message, "message of a null value");
    }

    #endregion // Methods
}