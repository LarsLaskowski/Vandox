using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// Names the kills that caused a snapshot.
/// </summary>
public sealed class SnapshotTrigger
{
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
#pragma warning disable RH2003, S2325
    internal FieldError? Validate(string path)
    {
        throw new NotImplementedException();
    }

#pragma warning restore RH2003, S2325

    #endregion // Methods
}