namespace Vandox.Core.Model;

/// <summary>
/// Marks a text property that is left out of the JSON document while it is empty.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class OmitWhenEmptyAttribute : Attribute;