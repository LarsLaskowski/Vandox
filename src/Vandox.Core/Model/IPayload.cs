using System.Text.Json.Serialization;

namespace Vandox.Core.Model;

/// <summary>
/// The kind-specific content of a record.
/// </summary>
public interface IPayload
{
    #region Properties

    /// <summary>
    /// Gets the kind of the payload, one of the <see cref="RecordKind"/> constants.
    /// </summary>
    [JsonIgnore]
    string Kind { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Checks the payload.
    /// </summary>
    /// <returns>The first rule that is broken, or <c>null</c></returns>
    FieldError? Validate();

    #endregion // Methods
}