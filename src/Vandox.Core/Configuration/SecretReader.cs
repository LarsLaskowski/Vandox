using System.Globalization;
using System.Text;

using Vandox.Core.IO;

namespace Vandox.Core.Configuration;

/// <summary>
/// Reads the secrets of the configuration from environment variables, directly or through <c>*_FILE</c> variables.
/// </summary>
internal static class SecretReader
{
    #region Constants

    private const string EnvPrefix = "VANDOX_";
    private const int MaxVariableNameBytes = 64;
    private const string ReasonUnreadable = "cannot read the file";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Rejects unknown and duplicate <c>VANDOX_</c> variables and returns the known ones.
    /// </summary>
    /// <param name="environment">The environment as name and value pairs</param>
    /// <param name="known">The full names of the known variables including the <c>_FILE</c> forms</param>
    /// <returns>The known variables</returns>
    /// <exception cref="ConfigException">A variable is unknown or set more than once</exception>
    internal static Dictionary<string, string> CheckEnvironment(IEnumerable<KeyValuePair<string, string>> environment, IReadOnlyCollection<string> known)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in environment)
        {
            if (name.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Register(result, name, value, known);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes.
    /// </summary>
    /// <param name="stream">The stream</param>
    /// <param name="limit">The most bytes to read</param>
    /// <returns>The bytes read</returns>
    internal static byte[] ReadAtMost(Stream stream, int limit)
    {
        var buffer = new byte[limit];
        var total = 0;
        int read;

        do
        {
            read = stream.Read(buffer, total, limit - total);
            total += read;
        }
        while (read > 0 && total < limit);

        return buffer[..total];
    }

    /// <summary>
    /// Reads the secret called <paramref name="name"/>, directly or through its <c>_FILE</c> variable.
    /// </summary>
    /// <param name="environment">The known variables</param>
    /// <param name="name">The name of the variable</param>
    /// <returns>The secret; not set when neither variable exists</returns>
    /// <exception cref="ConfigException">The value or the file is invalid</exception>
    internal static Secret Read(IReadOnlyDictionary<string, string> environment, string name)
    {
        var fileVariable = name + ConfigConstants.FileSuffix;
        var hasDirect = environment.TryGetValue(name, out var direct);
        var hasFile = environment.TryGetValue(fileVariable, out var path);

        if (hasDirect && hasFile)
        {
            throw ConfigException.ForSecret(name, $"{name} and {fileVariable} are both set, set only one");
        }

        if (hasFile)
        {
            return ReadFile(fileVariable, path ?? string.Empty);
        }

        return hasDirect ? FromValue(name, direct ?? string.Empty) : new Secret();
    }

    /// <summary>
    /// Reads the agent token and checks its minimum length.
    /// </summary>
    /// <param name="environment">The known variables</param>
    /// <param name="required">Whether a missing token is an error</param>
    /// <returns>The token; not set when it is missing and not required</returns>
    /// <exception cref="ConfigException">The token is invalid, or missing although required</exception>
    internal static Secret ReadAgentToken(IReadOnlyDictionary<string, string> environment, bool required)
    {
        var token = Read(environment, ConfigConstants.EnvAgentToken);

        if (token.IsSet)
        {
            if (token.Reveal().Length < ConfigConstants.MinAgentTokenBytes)
            {
                throw ConfigException.ForSecret(ConfigConstants.EnvAgentToken, $"must be at least {ConfigConstants.MinAgentTokenBytes} characters");
            }

            return token;
        }

        if (required)
        {
            throw ConfigException.ForSecret(ConfigConstants.EnvAgentToken, $"is required (set it or {ConfigConstants.EnvAgentToken}{ConfigConstants.FileSuffix})");
        }

        return new Secret();
    }

    /// <summary>
    /// Stores a known variable once.
    /// </summary>
    /// <param name="result">The known variables found so far</param>
    /// <param name="name">The name</param>
    /// <param name="value">The value</param>
    /// <param name="known">The names of the known variables</param>
    private static void Register(Dictionary<string, string> result, string name, string value, IReadOnlyCollection<string> known)
    {
        if (known.Contains(name))
        {
            if (result.TryAdd(name, value))
            {
                return;
            }

            throw ConfigException.ForSecret(name, "set more than once");
        }

        throw UnknownVariable(name);
    }

    /// <summary>
    /// Reports an unknown <c>VANDOX_</c> variable. The name is shown only when it is 1 to 64 characters of
    /// <c>[A-Za-z0-9_]</c>.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns>The error</returns>
    private static ConfigException UnknownVariable(string name)
    {
        if (name.Length <= MaxVariableNameBytes && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            return ConfigException.ForSecret(name, "unknown VANDOX_ variable");
        }

        return ConfigException.ForSecret(string.Empty, "unknown VANDOX_ variable (name not shown: only 1 to 64 characters of [A-Za-z0-9_] are shown)");
    }

    /// <summary>
    /// Checks a value and returns it as a secret. The value is never shown.
    /// </summary>
    /// <param name="variable">The variable the value came from</param>
    /// <param name="value">The value</param>
    /// <returns>The secret</returns>
    private static Secret FromValue(string variable, string value)
    {
        if (value.Length == 0)
        {
            throw ConfigException.ForSecret(variable, "is empty");
        }

        if (Encoding.UTF8.GetByteCount(value) > ConfigConstants.MaxSecretBytes)
        {
            throw ConfigException.ForSecret(variable, $"is longer than {ConfigConstants.MaxSecretBytes.ToString(CultureInfo.InvariantCulture)} bytes");
        }

        if (value.All(character => character is >= '!' and <= '~'))
        {
            return new Secret(value);
        }

        throw ConfigException.ForSecret(variable, "must consist of printable ASCII characters without spaces");
    }

    /// <summary>
    /// Reads a secret from the file at <paramref name="path"/>, the value of <paramref name="fileVariable"/>.
    /// Neither the path nor the content is shown in an error.
    /// </summary>
    /// <param name="fileVariable">The <c>_FILE</c> variable</param>
    /// <param name="path">The path</param>
    /// <returns>The secret</returns>
    private static Secret ReadFile(string fileVariable, string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            throw ConfigException.ForSecret(fileVariable, "must be an absolute path");
        }

        var kind = FileProbe.GetKind(path, followLinks: true);

        if (kind == FileKind.Missing)
        {
            throw ConfigException.ForSecret(fileVariable, ReasonUnreadable, "no such file or directory");
        }

        if (kind != FileKind.Regular)
        {
            throw ConfigException.ForSecret(fileVariable, "must name a regular file");
        }

        byte[] data;

        try
        {
            using var stream = FileProbe.OpenRegular(path, followLinks: true);

            data = ReadAtMost(stream, ConfigConstants.MaxSecretBytes + 3);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ConfigException.ForSecret(fileVariable, ReasonUnreadable, exception is UnauthorizedAccessException ? "permission denied" : null);
        }

        if (data.Length > ConfigConstants.MaxSecretBytes + 2)
        {
            throw ConfigException.ForSecret(fileVariable, $"file is larger than {ConfigConstants.MaxSecretBytes.ToString(CultureInfo.InvariantCulture)} bytes plus the line ending");
        }

        return FromValue(fileVariable, TrimLineEnding(Encoding.UTF8.GetString(data)));
    }

    /// <summary>
    /// Removes exactly one trailing line feed and, when that was removed, one carriage return before it.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns>The text without the line ending</returns>
    private static string TrimLineEnding(string text)
    {
        if (text.EndsWith('\n'))
        {
            var rest = text[..^1];

            return rest.EndsWith('\r') ? rest[..^1] : rest;
        }

        return text;
    }

    #endregion // Methods
}