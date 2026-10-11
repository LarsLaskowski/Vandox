namespace Vandox.Core.Signatures;

/// <summary>
/// A field a signature reads from a named group of its pattern.
/// </summary>
public sealed class SignatureField
{
    #region Constructors

    /// <summary>
    /// Creates a field.
    /// </summary>
    /// <param name="name">The field name, equal to the group name</param>
    /// <param name="type">The field type</param>
    internal SignatureField(string name, SignatureFieldType type)
    {
        Name = name;
        Type = type;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the field name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the field type.
    /// </summary>
    public SignatureFieldType Type { get; }

    #endregion // Properties
}