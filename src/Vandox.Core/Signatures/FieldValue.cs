namespace Vandox.Core.Signatures;

/// <summary>
/// The value of one field of an event: absent, a text or a number.
/// </summary>
/// <param name="Text">The text, or <c>null</c> for a number or an absent value</param>
/// <param name="Number">The number</param>
/// <param name="IsNumber">Whether the value is a number</param>
internal readonly record struct FieldValue(string? Text, long Number, bool IsNumber);