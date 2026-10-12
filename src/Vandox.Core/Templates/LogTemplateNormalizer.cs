using Vandox.Core.LogParsing;

namespace Vandox.Core.Templates;

/// <summary>
/// Turns a log message into its template: numbers, IDs, addresses, e-mail addresses, paths and hex values are replaced by
/// placeholders, so similar lines give the same text.
/// </summary>
public static class LogTemplateNormalizer
{
    #region Constants

    /// <summary>
    /// The longest template in UTF-8 bytes; a longer template is cut at a character boundary.
    /// </summary>
    public const int MaxTemplateBytes = 1024;

    /// <summary>
    /// The placeholder of a number.
    /// </summary>
    public const string NumberPlaceholder = "<num>";

    /// <summary>
    /// The placeholder of a hex value.
    /// </summary>
    public const string HexPlaceholder = "<hex>";

    /// <summary>
    /// The placeholder of an ID.
    /// </summary>
    public const string IdPlaceholder = "<id>";

    /// <summary>
    /// The placeholder of an IP address.
    /// </summary>
    public const string IpPlaceholder = "<ip>";

    /// <summary>
    /// The placeholder of an e-mail address.
    /// </summary>
    public const string EmailPlaceholder = "<email>";

    /// <summary>
    /// The placeholder of a path.
    /// </summary>
    public const string PathPlaceholder = "<path>";

    // The output buffer on the stack has this fixed size: a template is cut at 1,024 UTF-8 bytes, which are at most 1,024 chars,
    // so nothing beyond the first 1,024 chars is ever needed and no input can make the buffer grow.
    private const int StackBufferChars = 1024;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Returns the template of a log message.
    /// </summary>
    /// <param name="message">The message</param>
    /// <returns>The template, at most <see cref="MaxTemplateBytes"/> UTF-8 bytes</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c></exception>
    public static string Normalize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        Span<char> buffer = stackalloc char[StackBufferChars];

        var writer = new TemplateWriter(buffer);
        var text = message.AsSpan();
        var index = 0;

        while (index < text.Length && writer.HasRoom)
        {
            var value = text[index];

            if (IsSeparator(value))
            {
                writer.Separate();
                index++;
            }
            else if (IsDelimiter(value))
            {
                writer.Append(value);
                index++;
            }
            else
            {
                var end = index + 1;

                while (end < text.Length && IsTokenCharacter(text[end]))
                {
                    end++;
                }

                TokenRules.Write(text[index..end], ref writer);
                index = end;
            }
        }

        return Utf8Text.Cut(writer.Build(), MaxTemplateBytes, out _);
    }

    /// <summary>
    /// Tells whether a character separates tokens: white space and every other control character.
    /// </summary>
    /// <param name="value">The character</param>
    /// <returns><c>true</c> when the character is a separator</returns>
    private static bool IsSeparator(char value)
    {
        return char.IsWhiteSpace(value) || char.IsControl(value);
    }

    /// <summary>
    /// Tells whether a character belongs to a token: it is neither a separator nor a delimiter.
    /// </summary>
    /// <param name="value">The character</param>
    /// <returns><c>true</c> when the character belongs to a token</returns>
    private static bool IsTokenCharacter(char value)
    {
        if (IsSeparator(value) || IsDelimiter(value))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells whether a character is a delimiter, which is kept and ends a token.
    /// </summary>
    /// <param name="value">The character</param>
    /// <returns><c>true</c> when the character is a delimiter</returns>
    private static bool IsDelimiter(char value)
    {
        return value is '"' or '\'' or '(' or ')' or '[' or ']' or '{' or '}' or '<' or '>' or ',' or ';' or '=' or '#' or '!' or '?' or '*' or '&' or '|' or '^' or '$' or '\\' or '`';
    }

    #endregion // Methods
}