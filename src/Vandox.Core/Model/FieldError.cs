using System.Globalization;
using System.Text;

namespace Vandox.Core.Model;

/// <summary>
/// Reports the field of a record that violates a rule.
/// </summary>
public sealed class FieldError
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FieldError"/> class.
    /// </summary>
    /// <param name="field">Path of the field; empty for the record itself</param>
    /// <param name="reason">The rule that is broken; never a payload value</param>
    public FieldError(string field, string reason)
    {
        Field = field;
        Reason = reason;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the path of the field; empty for the record itself.
    /// </summary>
    public string Field { get; }

    /// <summary>
    /// Gets the rule that is broken.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets the text "model: Field: Reason", or "model: Reason" when the field is empty.
    /// </summary>
    public string Message => Field.Length == 0 ? $"model: {Reason}" : $"model: {Field}: {Reason}";

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Cuts <paramref name="value"/> to its first <see cref="ModelLimits.MaxNameBytes"/> UTF-8 bytes, quotes it and
    /// appends "..." when it was cut; for use in a field path or an error message.
    /// </summary>
    /// <param name="value">The value to quote</param>
    /// <returns>The quoted value</returns>
    public static string QuoteName(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        if (bytes.Length <= ModelLimits.MaxNameBytes)
        {
            return Quote(value);
        }

        var cut = Encoding.UTF8.GetString(bytes, 0, ModelLimits.MaxNameBytes).TrimEnd('�');

        return Quote(cut) + "...";
    }

    /// <summary>
    /// Returns a copy with <paramref name="path"/> prepended to the field.
    /// </summary>
    /// <param name="path">The path to prepend</param>
    /// <returns>The prefixed error</returns>
    public FieldError WithPrefix(string path)
    {
        if (Field.Length == 0)
        {
            return new FieldError(path, Reason);
        }

        var separator = Field.StartsWith('[') ? string.Empty : ".";

        return new FieldError($"{path}{separator}{Field}", Reason);
    }

    /// <summary>
    /// Quotes a string with double quotes, escaping control characters, quotes and backslashes.
    /// </summary>
    /// <param name="value">The value to quote</param>
    /// <returns>The quoted value</returns>
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);

        builder.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    {
                        builder.Append("\\\"");
                    }
                    break;

                case '\\':
                    {
                        builder.Append("\\\\");
                    }
                    break;

                case '\n':
                    {
                        builder.Append("\\n");
                    }
                    break;

                case '\r':
                    {
                        builder.Append("\\r");
                    }
                    break;

                case '\t':
                    {
                        builder.Append("\\t");
                    }
                    break;

                default:
                    {
                        if (char.IsControl(character))
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                    }
                    break;
            }
        }

        builder.Append('"');

        return builder.ToString();
    }

    #endregion // Methods

    #region Object

    /// <inheritdoc />
    public override string ToString()
    {
        return Message;
    }

    #endregion // Object
}