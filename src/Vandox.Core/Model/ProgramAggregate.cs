using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Sums the processes of one program.
/// </summary>
public sealed class ProgramAggregate
{
    #region Properties

    /// <summary>
    /// Gets or sets the program name.
    /// </summary>
    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of processes.
    /// </summary>
    [JsonPropertyName("count")]
    public uint Count { get; set; }

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
    /// Gets or sets a value indicating whether the resident set size counts shared memory more than once.
    /// </summary>
    [JsonPropertyName("rss_overcounted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RssOvercounted { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the aggregate.
    /// </summary>
    /// <param name="path">Path of the aggregate</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        var error = Check.RequiredShort($"{path}.program", Program);

        if (error is not null)
        {
            return error;
        }

        if (Count < 1)
        {
            return Check.Invalid($"{path}.count", "must be at least 1");
        }

        return Check.Rate($"{path}.cpu_percent", CpuPercent);
    }

    #endregion // Methods
}