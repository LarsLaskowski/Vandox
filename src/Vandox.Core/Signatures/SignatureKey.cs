using System.Runtime.CompilerServices;

namespace Vandox.Core.Signatures;

/// <summary>
/// The merge key of an event: the signature and its field values, compared one by one and never as a joined text.
/// </summary>
internal sealed class SignatureKey
{
    #region Fields

    private readonly FieldValue[] _values;
    private readonly int _hash;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Creates a key.
    /// </summary>
    /// <param name="signature">The signature</param>
    /// <param name="values">The field values in the order of the signature's fields</param>
    internal SignatureKey(Signature signature, FieldValue[] values)
    {
        Signature = signature;
        _values = values;

        var hash = RuntimeHelpers.GetHashCode(signature);

        foreach (var value in values)
        {
            hash = HashCode.Combine(hash, value);
        }

        _hash = hash;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the signature.
    /// </summary>
    internal Signature Signature { get; }

    /// <summary>
    /// Gets the field values in the order of the signature's fields.
    /// </summary>
    internal IReadOnlyList<FieldValue> Values => _values;

    #endregion // Properties

    #region Object

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is SignatureKey other && ReferenceEquals(Signature, other.Signature) && _hash == other._hash && _values.AsSpan().SequenceEqual(other._values);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return _hash;
    }

    #endregion // Object
}