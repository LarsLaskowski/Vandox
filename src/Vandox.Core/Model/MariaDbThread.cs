using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// One entry of the database's process list.
/// </summary>
public sealed class MariaDbThread
{
    #region Properties

    /// <summary>
    /// Gets or sets the thread ID.
    /// </summary>
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    /// <summary>
    /// Gets or sets the user.
    /// </summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the host.
    /// </summary>
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the database.
    /// </summary>
    [JsonPropertyName("db")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Db { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command.
    /// </summary>
    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the seconds the thread has been in its state.
    /// </summary>
    [JsonPropertyName("time_seconds")]
    public ulong TimeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the statement.
    /// </summary>
    [JsonPropertyName("info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Info { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the statement was cut.
    /// </summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the entry.
    /// </summary>
    /// <param name="path">Path of the entry</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        return Check.Short($"{path}.user", User)
                   ?? Check.Short($"{path}.host", Host)
                   ?? Check.Short($"{path}.db", Db)
                   ?? Check.Short($"{path}.command", Command)
                   ?? Check.Short($"{path}.state", State)
                   ?? Check.Text($"{path}.info", Info);
    }

    #endregion // Methods
}