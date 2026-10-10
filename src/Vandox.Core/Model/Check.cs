using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Vandox.Core.Model;

/// <summary>
/// Validation helpers shared by the payload types. Each helper returns the first rule that is broken, or <c>null</c>.
/// </summary>
internal static partial class Check
{
    #region Constants

    private const int UuidLength = 36;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates a <see cref="FieldError"/>.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="reason">The rule that is broken</param>
    /// <returns>The error</returns>
    internal static FieldError Invalid(string field, string reason)
    {
        return new FieldError(field, reason);
    }

    /// <summary>
    /// Returns the path of element <paramref name="index"/> of <paramref name="list"/>.
    /// </summary>
    /// <param name="list">Name of the list</param>
    /// <param name="index">Position of the element</param>
    /// <returns>The path</returns>
    internal static string Indexed(string list, int index)
    {
        return $"{list}[{index}]";
    }

    /// <summary>
    /// Returns the path of the entry <paramref name="key"/> of the map <paramref name="field"/>.
    /// </summary>
    /// <param name="field">Name of the map</param>
    /// <param name="key">Key of the entry</param>
    /// <returns>The path</returns>
    internal static string Keyed(string field, string key)
    {
        return $"{field}[{FieldError.QuoteName(key)}]";
    }

    /// <summary>
    /// Requires a non-empty text of at most <paramref name="limit"/> bytes that matches <paramref name="pattern"/>.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <param name="pattern">The pattern the text must match</param>
    /// <param name="limit">Maximum length in UTF-8 bytes</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Pattern(string field, string value, Regex pattern, int limit)
    {
        if (value.Length == 0)
        {
            return Invalid(field, "required");
        }

        if (Encoding.UTF8.GetByteCount(value) > limit)
        {
            return Invalid(field, "too long");
        }

        return pattern.IsMatch(value) ? null : Invalid(field, "invalid characters");
    }

    /// <summary>
    /// Requires a name.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The name</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Name(string field, string value)
    {
        return Pattern(field, value, NamePattern(), ModelLimits.MaxNameBytes);
    }

    /// <summary>
    /// Requires a name unless the text is empty.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The name</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? OptionalName(string field, string value)
    {
        return value.Length == 0 ? null : Name(field, value);
    }

    /// <summary>
    /// Requires a lower-case UUID.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Uuid(string field, string value)
    {
        return Pattern(field, value, UuidPattern(), UuidLength);
    }

    /// <summary>
    /// Requires a lower-case UUID unless the text is empty.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? OptionalUuid(string field, string value)
    {
        return value.Length == 0 ? null : Uuid(field, value);
    }

    /// <summary>
    /// Requires at most <paramref name="limit"/> UTF-8 bytes.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <param name="limit">Maximum length in UTF-8 bytes</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Length(string field, string value, int limit)
    {
        return Encoding.UTF8.GetByteCount(value) > limit ? Invalid(field, "too long") : null;
    }

    /// <summary>
    /// Requires a short text.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Short(string field, string value)
    {
        return Length(field, value, ModelLimits.MaxShortTextBytes);
    }

    /// <summary>
    /// Requires a text.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Text(string field, string value)
    {
        return Length(field, value, ModelLimits.MaxTextBytes);
    }

    /// <summary>
    /// Requires a non-empty short text.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The text</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? RequiredShort(string field, string value)
    {
        return value.Length == 0 ? Invalid(field, "required") : Short(field, value);
    }

    /// <summary>
    /// Requires at most <see cref="ModelLimits.MaxItems"/> entries.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="count">The number of entries</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Count(string field, int count)
    {
        return count > ModelLimits.MaxItems ? Invalid(field, "too many entries") : null;
    }

    /// <summary>
    /// Requires a time that is set and in UTC.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The time</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Time(string field, DateTimeOffset value)
    {
        if (value == default)
        {
            return Invalid(field, "required");
        }

        return value.Offset == TimeSpan.Zero ? null : Invalid(field, "must be UTC");
    }

    /// <summary>
    /// Requires a time in UTC unless it is not set.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The time</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? OptionalTime(string field, DateTimeOffset? value)
    {
        return value is null || value == default(DateTimeOffset) ? null : Time(field, value.Value);
    }

    /// <summary>
    /// Requires a finite number.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The number</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Finite(string field, double value)
    {
        return double.IsFinite(value) ? null : Invalid(field, "must be finite");
    }

    /// <summary>
    /// Requires a finite, non-negative number.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The number</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Rate(string field, double value)
    {
        var error = Finite(field, value);

        if (error is not null)
        {
            return error;
        }

        return value < 0 ? Invalid(field, "must not be negative") : null;
    }

    /// <summary>
    /// Requires an OOM score adjustment within -1000 and 1000 when it is set.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The adjustment</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? OomScoreAdj(string field, short? value)
    {
        return value is < -1000 or > 1000 ? Invalid(field, "out of range") : null;
    }

    /// <summary>
    /// Requires one of the allowed values.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The value</param>
    /// <param name="allowed">The allowed values</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? OneOf(string field, string value, params string[] allowed)
    {
        return Array.IndexOf(allowed, value) >= 0 ? null : Invalid(field, "unknown value");
    }

    /// <summary>
    /// Requires a valid IP address without a zone.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The address</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? Address(string field, IPAddress? value)
    {
        if (value is null)
        {
            return Invalid(field, "invalid address");
        }

        return value.AddressFamily == AddressFamily.InterNetworkV6 && value.ScopeId != 0 ? Invalid(field, "zone not allowed") : null;
    }

    /// <summary>
    /// Requires a valid IP address with a port, without a zone.
    /// </summary>
    /// <param name="field">Path of the field</param>
    /// <param name="value">The endpoint</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    internal static FieldError? AddressPort(string field, IPEndPoint? value)
    {
        return value is null ? Invalid(field, "invalid address") : Address(field, value.Address);
    }

    /// <summary>
    /// Returns the keys of <paramref name="map"/> in ordinal order, so the first reported error is deterministic.
    /// </summary>
    /// <typeparam name="TValue">Type of the values</typeparam>
    /// <param name="map">The map; may be <c>null</c></param>
    /// <returns>The sorted keys</returns>
    internal static IEnumerable<string> SortedKeys<TValue>(IReadOnlyDictionary<string, TValue>? map)
    {
        return map is null ? [] : map.Keys.Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates the pattern of a name: a letter or digit, then letters, digits and <c>._:/@+-</c>.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z", RegexOptions.CultureInvariant)]
    internal static partial Regex NamePattern();

    /// <summary>
    /// Creates the pattern of a lower-case UUID.
    /// </summary>
    /// <returns>The pattern</returns>
    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)]
    internal static partial Regex UuidPattern();

    #endregion // Methods
}