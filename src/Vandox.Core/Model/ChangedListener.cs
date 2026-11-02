using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

#pragma warning disable RH2003, S2325

/// <summary>
/// A known listener endpoint whose socket UID changed since the producer's previous run.
/// </summary>
public sealed class ChangedListener : Listener
{
    #region Properties

    /// <summary>
    /// Gets or sets the endpoint's lowest socket UID in the previous run.
    /// </summary>
    [JsonPropertyName("previous_uid")]
    [JsonPropertyOrder(103)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? PreviousUid { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint's sockets had several UIDs in the previous run.
    /// </summary>
    [JsonPropertyName("previous_mixed_uids")]
    [JsonPropertyOrder(104)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreviousMixedUids { get; set; }

    #endregion // Properties

    #region Listener

    /// <inheritdoc />
    internal override FieldError? Validate(string path)
    {
        throw new NotImplementedException();
    }

    #endregion // Listener
}