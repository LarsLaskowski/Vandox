using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Vandox.Core.Model;

/// <summary>
/// The state of the database at one moment.
/// </summary>
public sealed partial class MariaDbStatus : IPayload
{
    #region Constants

    /// <summary>
    /// The database answered.
    /// </summary>
    public const string Up = "up";

    /// <summary>
    /// The database is not running.
    /// </summary>
    public const string Down = "down";

    /// <summary>
    /// The database runs but did not answer.
    /// </summary>
    public const string NotAnswering = "not_answering";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets whether the database answered.
    /// </summary>
    [JsonPropertyName("availability")]
    public string Availability { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the latency of the ping in nanoseconds.
    /// </summary>
    [JsonPropertyName("ping_latency_ns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? PingLatencyNs { get; set; }

    /// <summary>
    /// Gets or sets the status counters.
    /// </summary>
    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, ulong>? Status { get; set; }

    /// <summary>
    /// Gets or sets the variables.
    /// </summary>
    [JsonPropertyName("variables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Variables { get; set; }

    /// <summary>
    /// Gets or sets the process list.
    /// </summary>
    [JsonPropertyName("threads")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MariaDbThread>? Threads { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sample is complete.
    /// </summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Creates the pattern of a status or variable name.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Checks the keys and values of the status, the variables and the threads.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    private FieldError? ValidateEntries()
    {
        foreach (var key in Check.SortedKeys(Status))
        {
            var error = Check.Pattern(Check.Keyed("status", key), key, KeyPattern(), ModelLimits.MaxNameBytes);

            if (error is not null)
            {
                return error;
            }
        }

        foreach (var key in Check.SortedKeys(Variables))
        {
            var path = Check.Keyed("variables", key);
            var error = Check.Pattern(path, key, KeyPattern(), ModelLimits.MaxNameBytes) ?? Check.Short(path, Variables![key]);

            if (error is not null)
            {
                return error;
            }
        }

        for (var index = 0; index < (Threads?.Count ?? 0); index++)
        {
            var error = Threads![index].Validate(Check.Indexed("threads", index));

            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    #endregion // Methods

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.MariaDbStatus;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var error = Check.OneOf("availability", Availability, Up, Down, NotAnswering);

        if (error is not null)
        {
            return error;
        }

        if (PingLatencyNs < 0)
        {
            return Check.Invalid("ping_latency_ns", "must not be negative");
        }

        var statusCount = Status?.Count ?? 0;
        var variableCount = Variables?.Count ?? 0;
        var threadCount = Threads?.Count ?? 0;

        error = Check.Count("status", statusCount) ?? Check.Count("variables", variableCount) ?? Check.Count("threads", threadCount);

        if (error is not null)
        {
            return error;
        }

        if (Availability != Up && (statusCount > 0 || variableCount > 0 || threadCount > 0))
        {
            return Check.Invalid("availability", "status, variables and threads must be empty unless up");
        }

        return ValidateEntries();
    }

    #endregion // IPayload
}