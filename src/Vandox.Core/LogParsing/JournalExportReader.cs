#pragma warning disable RH2003, S2325 // Skeleton: bodies are replaced by the implementation tasks

namespace Vandox.Core.LogParsing;

/// <summary>
/// Reads the entries of a journal export with bounded memory.
/// </summary>
internal sealed class JournalExportReader
{
    #region Constants

    /// <summary>
    /// The longest field name in bytes.
    /// </summary>
    internal const int MaxFieldNameBytes = 64;

    #endregion // Constants

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalExportReader"/> class.
    /// </summary>
    /// <param name="input">The content</param>
    internal JournalExportReader(Stream input)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Reads the next entry.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read</param>
    /// <returns>The entry, or <c>null</c> after the last one</returns>
    internal ValueTask<JournalEntry?> ReadAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}