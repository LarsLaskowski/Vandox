using System.Text.Json.Serialization;

namespace Vandox.Core.Configuration;

/// <summary>
/// Holds a secret value and never reveals it through formatting, logging or serialization. The only accessor is
/// <see cref="Reveal"/>.
/// </summary>
[JsonConverter(typeof(SecretJsonConverter))]
public sealed class Secret
{
    #region Constants

    /// <summary>
    /// The text printed in place of the value.
    /// </summary>
    public const string RedactedText = "[redacted]";

    #endregion // Constants

    #region Fields

    private readonly string? _value;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="Secret"/> class that is not set.
    /// </summary>
    public Secret()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Secret"/> class.
    /// </summary>
    /// <param name="value">The secret value</param>
    public Secret(string value)
    {
        _value = value;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the secret holds a non-empty value.
    /// </summary>
    public bool IsSet => _value is { Length: > 0 };

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the secret value, or an empty text when the secret is not set.
    /// </summary>
    /// <returns>The value</returns>
    public string Reveal()
    {
        return _value ?? string.Empty;
    }

    #endregion // Methods

    #region Object

    /// <inheritdoc />
    public override string ToString()
    {
        return RedactedText;
    }

    #endregion // Object
}