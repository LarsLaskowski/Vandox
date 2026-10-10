using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// One process of a <see cref="ProcessSnapshot"/>.
/// </summary>
public sealed class ProcessSample
{
    #region Properties

    /// <summary>
    /// Gets or sets the process ID.
    /// </summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// Gets or sets the parent process ID.
    /// </summary>
    [JsonPropertyName("ppid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Ppid { get; set; }

    /// <summary>
    /// Gets or sets the user.
    /// </summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command name.
    /// </summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the command line.
    /// </summary>
    [JsonPropertyName("cmdline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Cmdline { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the command line was cut.
    /// </summary>
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets the process state, one ASCII letter.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start of the process.
    /// </summary>
    [JsonPropertyName("started_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets the CPU usage in percent.
    /// </summary>
    [JsonPropertyName("cpu_percent")]
    public double CpuPercent { get; set; }

    /// <summary>
    /// Gets or sets the resident set size in bytes.
    /// </summary>
    [JsonPropertyName("rss_bytes")]
    public ulong RssBytes { get; set; }

    /// <summary>
    /// Gets or sets the proportional set size in bytes.
    /// </summary>
    [JsonPropertyName("pss_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? PssBytes { get; set; }

    /// <summary>
    /// Gets or sets the swap usage in bytes.
    /// </summary>
    [JsonPropertyName("swap_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? SwapBytes { get; set; }

    /// <summary>
    /// Gets or sets the OOM score adjustment.
    /// </summary>
    [JsonPropertyName("oom_score_adj")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public short? OomScoreAdj { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the sample.
    /// </summary>
    /// <param name="path">Path of the sample</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        if (Pid <= 0)
        {
            return Check.Invalid($"{path}.pid", "must be greater than 0");
        }

        if (Ppid < 0)
        {
            return Check.Invalid($"{path}.ppid", "must not be negative");
        }

        var error = Check.Short($"{path}.user", User)
                        ?? Check.RequiredShort($"{path}.command", Command)
                        ?? Check.Text($"{path}.cmdline", Cmdline);

        if (error is not null)
        {
            return error;
        }

        error = CheckState(path);

        return error
                   ?? Check.OptionalTime($"{path}.started_at", StartedAt)
                   ?? Check.Rate($"{path}.cpu_percent", CpuPercent)
                   ?? Check.OomScoreAdj($"{path}.oom_score_adj", OomScoreAdj);
    }

    /// <summary>
    /// Requires the state to be empty or one ASCII letter.
    /// </summary>
    /// <param name="path">Path of the sample</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    private FieldError? CheckState(string path)
    {
        if (State.Length == 0 || (State.Length == 1 && char.IsAsciiLetter(State[0])))
        {
            return null;
        }

        return Check.Invalid($"{path}.state", "must be one ASCII letter");
    }

    #endregion // Methods
}