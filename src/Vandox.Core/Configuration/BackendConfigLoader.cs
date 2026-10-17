using System.Globalization;
using System.Text;

using Vandox.Core.IO;
using Vandox.Core.LogParsing;
using Vandox.Core.Model;

namespace Vandox.Core.Configuration;

/// <summary>
/// Loads the configuration of vandoxd: one strictly parsed YAML file for the options, and the environment (directly or
/// through <c>*_FILE</c> files) for the secrets.
/// </summary>
public static class BackendConfigLoader
{
    #region Constants

    private const string KeyIngestListen = "ingest.listen";
    private const string TrustedProxiesReason = "must be a comma-separated list of at most 16 IP addresses or networks, such as 172.18.0.1 or 172.18.0.0/16";
    private const string ListenReason = "must be [host]:port with an empty host or an IP address and a port of 1 to 65535";
    private const int MaxPortDigits = 5;
    private const string WindowReason = "must be a whole number of minutes or hours from 1m to 24h, such as 30m or 1h";
    private const string LogFileReason = "must be an absolute, clean path of a file in a directory (no trailing slash, no . or .. elements)";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Reads the backend configuration file at <paramref name="path"/> and the secrets from <paramref name="environment"/>.
    /// </summary>
    /// <param name="path">The path of the configuration file</param>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <returns>The validated configuration</returns>
    /// <exception cref="ConfigException">The file, an option or a secret is invalid</exception>
    public static BackendConfig Load(string path, IEnumerable<KeyValuePair<string, string>> environment)
    {
        var text = ReadFile(path, ConfigConstants.MaxFileBytes);
        var config = new BackendConfig();
        var lines = StrictYamlDecoder.Decode(path, text, config);

        Validate(config, path, lines);

        string[] known = [
                             ConfigConstants.EnvAgentToken,
                             ConfigConstants.EnvAgentToken + ConfigConstants.FileSuffix,
                             ConfigConstants.EnvWebPasswordHash,
                             ConfigConstants.EnvWebPasswordHash + ConfigConstants.FileSuffix,
                             ConfigConstants.EnvTelegramBotToken,
                             ConfigConstants.EnvTelegramBotToken + ConfigConstants.FileSuffix
                         ];
        var variables = SecretReader.CheckEnvironment(environment, known);

        config.Secrets = new BackendSecrets
                         {
                             AgentToken = SecretReader.ReadAgentToken(variables, false),
                             WebPasswordHash = SecretReader.Read(variables, ConfigConstants.EnvWebPasswordHash),
                             TelegramBotToken = SecretReader.Read(variables, ConfigConstants.EnvTelegramBotToken)
                         };

        return config;
    }

    /// <summary>
    /// Returns the port of a listen address, or 0 when it is invalid.
    /// </summary>
    /// <param name="value">The address as <c>[host]:port</c></param>
    /// <returns>The port of 1 to 65535, or 0</returns>
    public static int ParseListenPort(string value)
    {
        var colon = value.LastIndexOf(':');

        if (colon < 0)
        {
            return 0;
        }

        var host = value[..colon];

        if (host.StartsWith('[') && host.EndsWith(']') && host.Length >= 2)
        {
            host = host[1..^1];
        }
        else if (host.Contains(':', StringComparison.Ordinal))
        {
            return 0;
        }

        if (host.Length == 0 || (host.IndexOf('%', StringComparison.Ordinal) < 0 && IpJson.TryParseAddress(host, out _)))
        {
            return ParsePort(value[(colon + 1)..]);
        }

        return 0;
    }

    /// <summary>
    /// Converts 1 to 5 ASCII digits to a port of 1 to 65535. A sign, a space or a name is not accepted.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The port, or 0 when the text is not a port</returns>
    private static int ParsePort(string text)
    {
        if (text.Length is >= 1 and <= MaxPortDigits && text.All(char.IsAsciiDigit))
        {
            var port = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

            return port is >= 1 and <= 65535 ? port : 0;
        }

        return 0;
    }

    /// <summary>
    /// Reads the regular file at <paramref name="path"/>, at most <paramref name="limit"/> bytes. The type is checked on the
    /// opened file (<c>FileProbe.OpenRegular</c> opens without blocking), so a FIFO cannot block the read; the check
    /// before the open only gives a missing or non-regular path its own message.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="limit">The largest accepted size</param>
    /// <returns>The text of the file</returns>
    private static string ReadFile(string path, int limit)
    {
        var kind = FileProbe.GetKind(path, followLinks: true);

        if (kind == FileKind.Missing)
        {
            throw new ConfigException($"config: {path}: no such file or directory");
        }

        if (kind != FileKind.Regular)
        {
            throw new ConfigException($"config: {path}: not a regular file");
        }

        byte[] data;

        try
        {
            using var stream = FileProbe.OpenRegular(path, followLinks: true);

            data = SecretReader.ReadAtMost(stream, limit + 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"config: {path}: cannot read the file");
        }

        if (data.Length > limit)
        {
            throw new ConfigException($"config: {path}: file is larger than {limit.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        return new UTF8Encoding(false, false).GetString(data);
    }

    /// <summary>
    /// Checks the options in order.
    /// </summary>
    /// <param name="config">The configuration</param>
    /// <param name="file">The path of the file, for error messages</param>
    /// <param name="lines">The lines of the keys the file set</param>
    private static void Validate(BackendConfig config, string file, Dictionary<string, int> lines)
    {
        var webPort = ParseListenPort(config.Web.Listen);

        Require(webPort > 0, file, lines, "web.listen", ListenReason);

        Require(TrustedProxyList.TryParse(config.Web.TrustedProxies, out _), file, lines, "web.trusted_proxies", TrustedProxiesReason);

        var ingestPort = ParseListenPort(config.Ingest.Listen);

        Require(ingestPort > 0, file, lines, KeyIngestListen, ListenReason);
        Require(webPort != ingestPort, file, lines, KeyIngestListen, "must not use the same port as web.listen");
        Require(IsCleanAbsolutePath(config.Storage.Directory), file, lines, "storage.directory", "must be an absolute, clean path (no trailing slash, no . or .. elements)");
        Require(config.Log.Level is "debug" or "info" or "warn" or "error", file, lines, "log.level", "must be one of debug, info, warn, error");
        Require(IsLogFilePath(config.Import.PleskPanelLog), file, lines, "import.plesk_panel_log", LogFileReason);
        Require(IsLogFilePath(config.Import.SwCpServerErrorLog), file, lines, "import.sw_cp_server_error_log", LogFileReason);
        Require(IsLogFilePath(config.Import.NginxErrorLog), file, lines, "import.nginx_error_log", LogFileReason);
        Require(IsLogFilePath(config.Import.ApacheErrorLog), file, lines, "import.apache_error_log", LogFileReason);
        Require(config.Import.TimeZone is null || SourceTimeZone.Find(config.Import.TimeZone) is not null,
                file,
                lines,
                "import.time_zone",
                "must be a time zone of the IANA time zone database, such as UTC or Europe/Berlin");
        Require(IsWindow(config.Incidents.WindowBefore), file, lines, "incidents.window_before", WindowReason);
        Require(IsWindow(config.Incidents.WindowAfter), file, lines, "incidents.window_after", WindowReason);
    }

    /// <summary>
    /// Tells whether an incident window option is a duration of 1 minute to 24 hours.
    /// </summary>
    /// <param name="value">The option value</param>
    /// <returns><c>true</c> when the value is in range</returns>
    private static bool IsWindow(string value)
    {
        return ConfigDuration.TryParse(value, out var duration) && duration >= TimeSpan.FromMinutes(1) && duration <= TimeSpan.FromHours(24);
    }

    /// <summary>
    /// Tells whether a path is absolute and clean: no trailing slash, no empty, <c>.</c> or <c>..</c> element.
    /// </summary>
    /// <param name="value">The path</param>
    /// <returns><c>true</c> when the path is absolute and clean</returns>
    private static bool IsCleanAbsolutePath(string value)
    {
        if (value.Length == 0 || value[0] != '/')
        {
            return false;
        }

        if (value == "/")
        {
            return true;
        }

        return value[1..].Split('/').All(element => element.Length > 0 && element != "." && element != "..");
    }

    /// <summary>
    /// Tells whether a path is an absolute, clean path of a file in a directory (at least two elements).
    /// </summary>
    /// <param name="value">The path</param>
    /// <returns><c>true</c> when the path names a file in a directory</returns>
    private static bool IsLogFilePath(string value)
    {
        return IsCleanAbsolutePath(value) && value.AsSpan(1).Contains('/');
    }

    /// <summary>
    /// Throws an error for a key, at the line the decoder recorded for it (0 when the file did not set it), unless the
    /// condition holds.
    /// </summary>
    /// <param name="condition">The rule</param>
    /// <param name="file">The path of the file</param>
    /// <param name="lines">The lines of the keys the file set</param>
    /// <param name="key">The key</param>
    /// <param name="reason">What is wrong</param>
    private static void Require(bool condition, string file, Dictionary<string, int> lines, string key, string reason)
    {
        if (condition)
        {
            return;
        }

        throw ConfigException.ForKey(file, lines.GetValueOrDefault(key), key, reason);
    }

    #endregion // Methods
}