using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The process list at one moment.
/// </summary>
public sealed class ProcessSnapshot : IPayload
{
    #region Properties

    /// <summary>
    /// Gets or sets a value indicating whether the list is complete.
    /// </summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; set; }

    /// <summary>
    /// Gets or sets the total number of processes on the server.
    /// </summary>
    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? Total { get; set; }

    /// <summary>
    /// Gets or sets the sampled processes.
    /// </summary>
    [JsonPropertyName("processes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProcessSample>? Processes { get; set; }

    /// <summary>
    /// Gets or sets the sums per program.
    /// </summary>
    [JsonPropertyName("programs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ProgramAggregate>? Programs { get; set; }

    #endregion // Properties

    #region IPayload

    /// <inheritdoc />
    public string Kind => RecordKind.ProcessSnapshot;

    /// <inheritdoc />
    public FieldError? Validate()
    {
        var processCount = Processes?.Count ?? 0;
        var programCount = Programs?.Count ?? 0;

        if (processCount == 0 && programCount == 0)
        {
            return Check.Invalid("processes", "at least one process or program required");
        }

        var error = Check.Count("processes", processCount) ?? Check.Count("programs", programCount);

        if (error is not null)
        {
            return error;
        }

        var pids = new HashSet<int>();

        for (var index = 0; index < processCount; index++)
        {
            var sample = Processes![index];

            error = sample.Validate(Check.Indexed("processes", index));

            if (error is not null)
            {
                return error;
            }

            if (pids.Add(sample.Pid))
            {
                continue;
            }

            return Check.Invalid($"{Check.Indexed("processes", index)}.pid", "duplicate pid");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < programCount; index++)
        {
            var aggregate = Programs![index];

            error = aggregate.Validate(Check.Indexed("programs", index));

            if (error is not null)
            {
                return error;
            }

            if (names.Add(aggregate.Program))
            {
                continue;
            }

            return Check.Invalid($"{Check.Indexed("programs", index)}.program", "duplicate program");
        }

        return null;
    }

    #endregion // IPayload
}