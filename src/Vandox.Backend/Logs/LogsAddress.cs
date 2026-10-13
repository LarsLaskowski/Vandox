using System.Globalization;
using System.Text;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Logs;

/// <summary>
/// The filters of the logs view as they appear in the address.
/// </summary>
public sealed class LogsAddress
{
    #region Constants

    private const int MaxValueBytes = 1024;

    private const int MaxTemplateDigits = 18;

    private const string TimeCharacters = "0123456789-T:";

    private const string TextName = "q";

    private const string SourceName = "source";

    private const string ProgramName = "program";

    private const string SeverityName = "severity";

    private const string TemplateName = "template";

    private const string FromName = "from";

    private const string ToName = "to";

    private static readonly string[] _names = [TextName, SourceName, ProgramName, SeverityName, TemplateName, FromName, ToName];

    private static readonly string[] _timeFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program (service).
    /// </summary>
    public string Program { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the highest priority to show.
    /// </summary>
    public byte? Severity { get; set; }

    /// <summary>
    /// Gets or sets the template number; 0 for none.
    /// </summary>
    public long TemplateId { get; set; }

    /// <summary>
    /// Gets or sets the start of the range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of the range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the error of the address; empty when it is valid.
    /// </summary>
    public string Error { get; set; } = string.Empty;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reads the filters from a query string.
    /// </summary>
    /// <param name="query">The query string, with or without the leading question mark</param>
    /// <returns>The filters and the error of the address</returns>
    public static LogsAddress Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var address = new LogsAddress();
        var values = QueryHelpers.ParseQuery(query);

        address.Error = address.Read(values);

        return address;
    }

    /// <summary>
    /// Reads the filters from the text of the fields of a form; an empty text is no filter.
    /// </summary>
    /// <param name="text">The search text</param>
    /// <param name="source">The source</param>
    /// <param name="program">The program</param>
    /// <param name="severity">The name of the highest priority</param>
    /// <param name="from">The start of the range as in the address</param>
    /// <param name="to">The end of the range as in the address</param>
    /// <returns>The filters and the error of the values</returns>
    public static LogsAddress FromFields(string text, string source, string program, string severity, string from, string to)
    {
        var address = new LogsAddress
                      {
                          Text = text ?? string.Empty,
                          Source = source ?? string.Empty,
                          Program = program ?? string.Empty
                      };

        address.Error = address.ReadField(SeverityName, severity) is { Length: > 0 } severityError ? severityError : address.ReadFields(from, to);

        if (address.Error.Length == 0)
        {
            address.Error = address.Validate();
        }

        return address;
    }

    /// <summary>
    /// Writes a time in the form of the address; the seconds only when they are not 0.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text in UTC</returns>
    public static string FormatTime(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString(time.UtcDateTime.Second == 0 ? "yyyy-MM-dd'T'HH:mm" : "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks the current values.
    /// </summary>
    /// <returns>The error text, or an empty text when the values are valid</returns>
    public string Validate()
    {
        var checks = new[]
                     {
                         (TextName, IsValidText(Text)),
                         (SourceName, IsValidText(Source)),
                         (ProgramName, IsValidText(Program)),
                         (SeverityName, Severity is null or <= 7),
                         (TemplateName, TemplateId >= 0),
                         (FromName, From is null || StorableTime.Contains(From.Value)),
                         (ToName, To is null || StorableTime.Contains(To.Value))
                     };

        foreach (var (name, valid) in checks)
        {
            if (valid)
            {
                continue;
            }

            return Format(LogsView.AddressInvalidValueFormat, name);
        }

        return From is not null && To is not null && From.Value >= To.Value ? LogsView.AddressRangeReversed : string.Empty;
    }

    /// <summary>
    /// Writes the filters as a query string.
    /// </summary>
    /// <returns>The query string with its leading question mark, or an empty text when no filter is set</returns>
    /// <exception cref="InvalidOperationException">A value is not valid</exception>
    public string ToQuery()
    {
        var error = Validate();

        if (error.Length > 0)
        {
            throw new InvalidOperationException(error);
        }

        var parts = new List<string>();

        Add(parts, TextName, Text);
        Add(parts, SourceName, Source);
        Add(parts, ProgramName, Program);
        Add(parts, SeverityName, Severity is null ? string.Empty : Severities.Name(Severity.Value));
        Add(parts, TemplateName, TemplateId > 0 ? TemplateId.ToString(CultureInfo.InvariantCulture) : string.Empty);
        Add(parts, FromName, From is null ? string.Empty : FormatTime(From.Value));
        Add(parts, ToName, To is null ? string.Empty : FormatTime(To.Value));

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Builds the store filter of the address.
    /// </summary>
    /// <param name="limit">The page size</param>
    /// <param name="scanLimit">The scan bound</param>
    /// <param name="after">The position to continue from</param>
    /// <returns>The filter</returns>
    public LogFilter ToFilter(int limit, int scanLimit, LogPosition? after)
    {
        return new LogFilter
               {
                   Text = Text,
                   Source = Source,
                   Program = Program,
                   MaxPriority = Severity,
                   TemplateId = TemplateId,
                   From = From ?? StorableTime.Min,
                   To = To ?? StorableTime.Max,
                   After = after,
                   Limit = limit,
                   ScanLimit = scanLimit
               };
    }

    /// <summary>
    /// Formats a user text with one argument.
    /// </summary>
    /// <param name="format">The format</param>
    /// <param name="argument">The argument</param>
    /// <returns>The text</returns>
    private static string Format(string format, string argument)
    {
        return string.Format(CultureInfo.InvariantCulture, format, argument);
    }

    /// <summary>
    /// Tells whether a text is valid UTF-16 of at most 1,024 UTF-8 bytes without control, format, line separator and paragraph separator characters.
    /// </summary>
    /// <param name="text">The text</param>
    /// <returns><c>true</c> when the text is valid</returns>
    private static bool IsValidText(string text)
    {
        var bytes = 0;
        var index = 0;

        while (index < text.Length)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                return false;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                return false;
            }

            bytes += rune.Utf8SequenceLength;
            index += consumed;
        }

        return bytes <= MaxValueBytes;
    }

    /// <summary>
    /// Reads a time in the form of the address.
    /// </summary>
    /// <param name="value">The text</param>
    /// <param name="time">The time in UTC</param>
    /// <returns><c>true</c> when the text is a time of the storable range in one of the two forms</returns>
    private static bool TryReadTime(string value, out DateTimeOffset time)
    {
        time = default;

        return value.All(character => TimeCharacters.Contains(character, StringComparison.Ordinal))
               && DateTimeOffset.TryParseExact(value, _timeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)
               && StorableTime.Contains(time);
    }

    /// <summary>
    /// Reads a template number: one to eighteen ASCII digits, at least 1.
    /// </summary>
    /// <param name="value">The text</param>
    /// <param name="id">The number</param>
    /// <returns><c>true</c> when the text is a valid number</returns>
    private static bool TryReadTemplate(string value, out long id)
    {
        id = 0;

        return value.Length is >= 1 and <= MaxTemplateDigits
               && value.All(char.IsAsciiDigit)
               && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
               && id >= 1;
    }

    /// <summary>
    /// Adds a parameter unless its value is empty.
    /// </summary>
    /// <param name="parts">The parts of the query</param>
    /// <param name="name">The name of the parameter</param>
    /// <param name="value">The value, not yet escaped</param>
    private static void Add(List<string> parts, string name, string value)
    {
        if (value.Length > 0)
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }

    /// <summary>
    /// Checks a value and stores it.
    /// </summary>
    /// <typeparam name="T">The type of the value</typeparam>
    /// <param name="value">The value</param>
    /// <param name="check">The rule of the value</param>
    /// <param name="store">Stores the value</param>
    /// <returns><c>true</c> when the value passed the rule</returns>
    private static bool Set<T>(T value, Func<T, bool> check, Action<T> store)
    {
        if (check(value))
        {
            store(value);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the known parameters.
    /// </summary>
    /// <param name="values">The decoded query</param>
    /// <returns>The error of the first parameter that is repeated or invalid, or of the range, or an empty text</returns>
    private string Read(Dictionary<string, StringValues> values)
    {
        foreach (var name in _names)
        {
            if (values.TryGetValue(name, out var found))
            {
                var error = found.Count > 1 ? Format(LogsView.AddressRepeatedFormat, name) : ReadField(name, found[0] ?? string.Empty);

                if (error.Length > 0)
                {
                    return error;
                }
            }
        }

        return From is not null && To is not null && From.Value >= To.Value ? LogsView.AddressRangeReversed : string.Empty;
    }

    /// <summary>
    /// Reads the two time fields of a form.
    /// </summary>
    /// <param name="from">The start of the range</param>
    /// <param name="to">The end of the range</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadFields(string from, string to)
    {
        var error = ReadField(FromName, from ?? string.Empty);

        return error.Length > 0 ? error : ReadField(ToName, to ?? string.Empty);
    }

    /// <summary>
    /// Reads the value of one parameter; an empty value is absent.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <param name="value">The decoded value</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadField(string name, string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var valid = name switch
                    {
                        TextName => Set(value, IsValidText, text => Text = text),
                        SourceName => Set(value, IsValidText, text => Source = text),
                        ProgramName => Set(value, IsValidText, text => Program = text),
                        SeverityName => Severities.TryParse(value, out var priority) && Set(priority, _ => true, found => Severity = found),
                        TemplateName => TryReadTemplate(value, out var id) && Set(id, _ => true, found => TemplateId = found),
                        FromName => TryReadTime(value, out var from) && Set(from, _ => true, found => From = found),
                        _ => TryReadTime(value, out var to) && Set(to, _ => true, found => To = found)
                    };

        return valid ? string.Empty : Format(LogsView.AddressInvalidValueFormat, name);
    }

    #endregion // Methods
}