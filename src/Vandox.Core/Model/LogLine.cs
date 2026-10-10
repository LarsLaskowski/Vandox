using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// One line of a log.
/// </summary>
public sealed class LogLine : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets the name of the log.
    /// </summary>
    [JsonPropertyName("log")]
    public string Log { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the host that wrote the line.
    /// </summary>
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program that wrote the line.
    /// </summary>
    [JsonPropertyName("program")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the process ID of the program.
    /// </summary>
    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the syslog priority (0 to 7).
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte? Priority { get; set; }

    /// <summary>
    /// Gets or sets the message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the message was cut.
    /// </summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets the event a producer recognized in the line, e.g. <c>mariadb.start</c>; empty for none.
    /// </summary>
    [JsonPropertyName("event")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Event { get; set; } = string.Empty;

    #endregion // Properties

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.LogLine;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.RequiredShort("log", Log) ?? Check.Short("host", Host) ?? Check.Short("program", Program) ?? Check.OptionalName("event", Event);

        if (error is not null)
        {
            return error;
        }

        if (Pid < 0)
        {
            return Check.Invalid("pid", "must not be negative");
        }

        if (Priority > 7)
        {
            return Check.Invalid("priority", "must be at most 7");
        }

        return Check.Text("message", Message);
    }

    #endregion // IPayload
}