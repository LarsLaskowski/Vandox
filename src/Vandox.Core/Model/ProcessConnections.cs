using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Counts sockets per process.
/// </summary>
public sealed class ProcessConnections
{
    #region Properties

    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the command name.
    /// </summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of sockets.
    /// </summary>
    [JsonPropertyName("count")]
    public uint Count { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        if (Pid <= 0)
        {
            return Check.Invalid($"{path}.pid", "must be greater than 0");
        }

        return Check.RequiredShort($"{path}.command", Command) ?? ConnectionSnapshot.CheckCount(path, Count);
    }

    #endregion // Methods
}