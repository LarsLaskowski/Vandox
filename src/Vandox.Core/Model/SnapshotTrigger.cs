using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Names the kills that caused a snapshot.
/// </summary>
public sealed class SnapshotTrigger
{
    #region Constants

    /// <summary>
    /// The most kills a trigger lists.
    /// </summary>
    public const int MaxKills = 64;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the kills.
    /// </summary>
    [JsonPropertyName("kills")]
    public List<KillRef>? Kills { get; set; }

    /// <summary>
    /// Gets or sets the number of kills left out of the list.
    /// </summary>
    [JsonPropertyName("kills_omitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint KillsOmitted { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the details.
    /// </summary>
    /// <param name="path">The field path of the trigger in the record</param>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    internal FieldError? Validate(string path)
    {
        var field = $"{path}.kills";
        var count = Kills?.Count ?? 0;

        if (count == 0)
        {
            return Check.Invalid(field, "at least one kill required");
        }

        if (count > MaxKills)
        {
            return Check.Invalid(field, "too many entries");
        }

        var seen = new HashSet<(string BootId, long KernelTimeNs, int VictimPid)>();

        for (var index = 0; index < count; index++)
        {
            var kill = Kills![index];
            var error = kill.Validate(Check.Indexed(field, index));

            if (error is not null)
            {
                return error;
            }

            if (seen.Add((kill.BootId, kill.KernelTimeNs, kill.VictimPid)))
            {
                continue;
            }

            return Check.Invalid(Check.Indexed(field, index), "duplicate kill");
        }

        return null;
    }

    #endregion // Methods
}