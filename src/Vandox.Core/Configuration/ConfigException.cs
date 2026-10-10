namespace Vandox.Core.Configuration;

/// <summary>
/// Reports a problem in the configuration file or in a secret. The message never contains the value, a secret file
/// path or any other document text.
/// </summary>
public sealed class ConfigException : Exception
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigException"/> class.
    /// </summary>
    /// <param name="message">The message, starting with "config: "</param>
    public ConfigException(string message)
        : base(message)
    {
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Creates an error for a problem in the configuration file, at a key or at a line.
    /// </summary>
    /// <param name="file">The path as given to the loader</param>
    /// <param name="line">The 1-based line; 0 when the key is missing or the parser reported no line</param>
    /// <param name="key">The dotted key path, e.g. <c>web.listen</c>; empty for document-level problems</param>
    /// <param name="reason">What is wrong; never the value or any other document text</param>
    /// <returns>The error</returns>
    public static ConfigException ForKey(string file, int line, string key, string reason)
    {
        var position = line > 0 ? $":{line}" : string.Empty;
        var name = key.Length > 0 ? $"{key}: " : string.Empty;

        return new ConfigException($"config: {file}{position}: {name}{reason}");
    }

    /// <summary>
    /// Creates an error for a problem with a secret's environment variable or file.
    /// </summary>
    /// <param name="variable">The variable, e.g. <c>VANDOX_AGENT_TOKEN_FILE</c>; empty for an unknown name that is not shown</param>
    /// <param name="reason">What is wrong; never the value, a file path or the file content</param>
    /// <param name="errno">The operating system error text, if any</param>
    /// <returns>The error</returns>
    public static ConfigException ForSecret(string variable, string reason, string? errno = null)
    {
        var name = variable.Length > 0 ? $"{variable}: " : string.Empty;
        var detail = errno is null ? string.Empty : $": {errno}";

        return new ConfigException($"config: {name}{reason}{detail}");
    }

    #endregion // Methods
}