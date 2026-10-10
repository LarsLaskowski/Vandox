namespace Vandox.Core.LogParsing;

/// <summary>
/// What <see cref="JournalExportReader"/> found at the start of a field.
/// </summary>
internal enum JournalFieldKind
{
    /// <summary>
    /// The input ended at a field boundary.
    /// </summary>
    End,

    /// <summary>
    /// An empty line, which ends the entry.
    /// </summary>
    EmptyLine,

    /// <summary>
    /// A text field, <c>NAME=value</c>.
    /// </summary>
    Text,

    /// <summary>
    /// A binary field: the name, a line feed, a length and the bytes.
    /// </summary>
    Binary,

    /// <summary>
    /// A field name that is not valid.
    /// </summary>
    Malformed,

    /// <summary>
    /// The input ended inside a field.
    /// </summary>
    Cut
}