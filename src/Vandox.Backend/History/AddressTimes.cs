using System.Text.RegularExpressions;

using Microsoft.Extensions.Primitives;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.History;

/// <summary>
/// The range and the moment of an address: the preset, the start, the end and the moment, with their reading, checks and writing.
/// </summary>
internal sealed partial class AddressTimes
{
    #region Constants

    /// <summary>
    /// The most bytes of a source and of the name of a metric.
    /// </summary>
    private const int MaxPartBytes = 128;

    private const string RangeName = "range";

    private const string FromName = "from";

    private const string ToName = "to";

    private const string AtName = "at";

    private const string RepeatedFormat = "The address gives the parameter {0} more than once.";

    private const string InvalidFormat = "The address has an invalid value for the parameter {0}.";

    private const string BothKinds = "The address gives both a preset range and a start or end time.";

    private const string NeedsBoth = "The address needs both a start and an end time.";

    private const string Reversed = "The address has a start time that is not before its end time.";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the preset range.
    /// </summary>
    public HistoryPreset? Preset { get; set; }

    /// <summary>
    /// Gets or sets the start of a fixed range.
    /// </summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>
    /// Gets or sets the end of a fixed range.
    /// </summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>
    /// Gets or sets the moment.
    /// </summary>
    public DateTimeOffset? At { get; set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Tells whether a name is valid: 1 to 128 bytes of the name pattern of the wire format.
    /// </summary>
    /// <param name="name">The name</param>
    /// <returns><c>true</c> when the name is valid</returns>
    internal static bool IsValidName(string? name)
    {
        return name is { Length: >= 1 and <= MaxPartBytes } && NamePattern().IsMatch(name);
    }

    /// <summary>
    /// Builds the error of an invalid value.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    internal static string Invalid(string name)
    {
        return HistoryView.Fill(InvalidFormat, name);
    }

    /// <summary>
    /// Builds the error of a parameter given more than once.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The text</returns>
    internal static string Repeated(string name)
    {
        return HistoryView.Fill(RepeatedFormat, name);
    }

    /// <summary>
    /// Reads a single-valued name parameter such as the source.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <param name="name">The name of the parameter</param>
    /// <returns>The error (an empty text for none) and the value, <see langword="null"/> when the parameter is absent or empty</returns>
    internal static ValueTuple<string, string?> ReadName(Dictionary<string, StringValues> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (values.TryGetValue(name, out var found) == false)
        {
            return (string.Empty, null);
        }

        if (found.Count > 1)
        {
            return (Repeated(name), null);
        }

        var value = found[0] ?? string.Empty;

        if (value.Length == 0)
        {
            return (string.Empty, null);
        }

        return IsValidName(value) ? (string.Empty, value) : (Invalid(name), null);
    }

    /// <summary>
    /// Reads the parameters range, from, to and at, then checks how the range goes together.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    internal string ReadParameters(Dictionary<string, StringValues> values)
    {
        var error = ReadValues(values);

        return error.Length > 0 ? error : ValidateRange();
    }

    /// <summary>
    /// Reads the parameters range, from, to and at in this order, without checking how the range goes together.
    /// </summary>
    /// <param name="values">The query values</param>
    /// <returns>The first error, or an empty text</returns>
    internal string ReadValues(Dictionary<string, StringValues> values)
    {
        foreach (var name in new[] { RangeName, FromName, ToName, AtName })
        {
            if (values.TryGetValue(name, out var found) == false)
            {
                continue;
            }

            var error = found.Count > 1 ? Repeated(name) : ReadValue(name, found[0] ?? string.Empty);

            if (error.Length > 0)
            {
                return error;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Checks how the preset, the start and the end go together.
    /// </summary>
    /// <returns>The error, or an empty text</returns>
    internal string ValidateRange()
    {
        if (Preset is not null && (From is not null || To is not null))
        {
            return BothKinds;
        }

        if ((From is null) != (To is null))
        {
            return NeedsBoth;
        }

        return From >= To ? Reversed : string.Empty;
    }

    /// <summary>
    /// Checks every value again.
    /// </summary>
    /// <returns>The first error, or an empty text</returns>
    internal string Validate()
    {
        if ((From is not null && StorableTime.Contains(From.Value) == false) || (To is not null && StorableTime.Contains(To.Value) == false))
        {
            return Invalid(From is not null && StorableTime.Contains(From.Value) == false ? FromName : ToName);
        }

        if (At is not null && StorableTime.Contains(At.Value) == false)
        {
            return Invalid(AtName);
        }

        return ValidateRange();
    }

    /// <summary>
    /// Appends the parameters range, from, to and at to the parts of a query string.
    /// </summary>
    /// <param name="parts">The parts</param>
    internal void AppendTo(List<string> parts)
    {
        if (Preset is not null)
        {
            parts.Add($"{RangeName}={HistoryView.PresetValue(Preset.Value)}");
        }

        if (From is not null && To is not null)
        {
            parts.Add($"{FromName}={LogsAddress.FormatTime(From.Value)}");
            parts.Add($"{ToName}={LogsAddress.FormatTime(To.Value)}");
        }

        if (At is not null)
        {
            parts.Add($"{AtName}={LogsAddress.FormatTime(At.Value)}");
        }
    }

    /// <summary>
    /// Writes an address as a query string: the source, the times and an optional last parameter.
    /// </summary>
    /// <param name="error">The error found by the checks of the address, empty when there is none</param>
    /// <param name="sourceName">The name of the source parameter</param>
    /// <param name="source">The source, or <c>null</c> for the default</param>
    /// <param name="last">The parameter written after the times, or <c>null</c> for none</param>
    /// <returns>The query string, empty for the default address</returns>
    /// <exception cref="InvalidOperationException">The address has an error</exception>
    internal string ToQuery(string error, string sourceName, string? source, string? last)
    {
        if (error.Length > 0)
        {
            throw new InvalidOperationException(error);
        }

        var parts = new List<string>();

        if (source is not null)
        {
            parts.Add($"{sourceName}={Uri.EscapeDataString(source)}");
        }

        AppendTo(parts);

        if (last is not null)
        {
            parts.Add(last);
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    /// <summary>
    /// Gives the range: the fixed one, else the preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <returns>The range</returns>
    internal HistoryRange Range(DateTimeOffset now)
    {
        if (From is not null && To is not null)
        {
            return new HistoryRange(From.Value, To.Value);
        }

        return new HistoryRange(now - HistoryView.PresetSpan(Preset ?? HistoryPreset.OneDay), now);
    }

    /// <summary>
    /// Gives the range: the fixed one, else a preset up to now.
    /// </summary>
    /// <param name="now">The current time</param>
    /// <param name="fallback">The preset of an address without a range</param>
    /// <returns>The range</returns>
#pragma warning disable S2325, RH2003 // Skeleton: removed with the implementation
    internal HistoryRange Range(DateTimeOffset now, HistoryPreset fallback)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003 // Skeleton: removed with the implementation

    /// <summary>
    /// Checks whether another set has the same preset, start and end.
    /// </summary>
    /// <param name="other">The other set</param>
    /// <returns><c>true</c> when the ranges are the same</returns>
    internal bool SameRange(AddressTimes other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Preset == other.Preset && From == other.From && To == other.To;
    }

    /// <summary>
    /// Copies the set.
    /// </summary>
    /// <returns>The copy</returns>
    internal AddressTimes Copy()
    {
        return new AddressTimes
               {
                   Preset = Preset,
                   From = From,
                   To = To,
                   At = At
               };
    }

    /// <summary>
    /// Reads a preset value.
    /// </summary>
    /// <param name="value">The value</param>
    /// <param name="preset">The preset</param>
    /// <returns><c>true</c> when the value names a preset</returns>
    private static bool TryReadPreset(string value, out HistoryPreset preset)
    {
        var found = Enum.GetValues<HistoryPreset>().Cast<HistoryPreset?>().FirstOrDefault(candidate => string.Equals(HistoryView.PresetValue(candidate!.Value), value, StringComparison.Ordinal));

        preset = found.GetValueOrDefault();

        return found is not null;
    }

    /// <summary>
    /// The pattern of a source and of the name of a metric: the name pattern of the wire format, ending in <c>\z</c> so that a trailing line feed is refused.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>
    /// Reads one single-valued parameter.
    /// </summary>
    /// <param name="name">The name of the parameter</param>
    /// <param name="value">The value; empty is no value</param>
    /// <returns>The error, or an empty text</returns>
    private string ReadValue(string name, string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var valid = false;

        if (name == RangeName)
        {
            valid = TryReadPreset(value, out var preset);
            Preset = valid ? preset : null;
        }
        else if (LogsAddress.TryReadTime(value, out var time))
        {
            valid = true;

            switch (name)
            {
                case FromName:
                    From = time;
                    break;

                case ToName:
                    To = time;
                    break;

                default:
                    At = time;
                    break;
            }
        }

        return valid ? string.Empty : Invalid(name);
    }

    #endregion // Methods
}