namespace Vandox.Core.Templates;

/// <summary>
/// Turns a log message into its template: numbers, IDs, addresses, e-mail addresses, paths and hex values are replaced by
/// placeholders, so similar lines give the same text.
/// </summary>
#pragma warning disable RH2003, S2325, S1172
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
        throw new NotImplementedException();
    }

    #endregion // Methods
}