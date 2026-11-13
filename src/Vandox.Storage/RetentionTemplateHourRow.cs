namespace Vandox.Storage;

/// <summary>
/// A template hour found by the removal walk.
/// </summary>
/// <param name="TemplateId">The template ID</param>
/// <param name="Hour">The start of the hour in nanoseconds</param>
/// <param name="Source">The source of the template</param>
internal readonly record struct RetentionTemplateHourRow(long TemplateId, long Hour, string Source);