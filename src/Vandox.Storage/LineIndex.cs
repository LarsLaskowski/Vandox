using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

#pragma warning disable RH2003, S2325

/// <summary>
/// A private in-memory FTS5 index for the lines of one chunk of a scan; it matches multi-part terms with the tokenizer of the stored index.
/// The connection is disposed on every path.
/// </summary>
internal sealed class LineIndex : IDisposable
{
    #region Constants

    /// <summary>
    /// The largest number of lines inserted in one chunk.
    /// </summary>
    internal const int ChunkLines = 64;

    /// <summary>
    /// The tokenizer of the stored index.
    /// </summary>
    internal const string Tokenizer = "unicode61 remove_diacritics 2";

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Opens an empty in-memory index.
    /// </summary>
    /// <param name="cancellationToken">Cancels the calls on the index</param>
    /// <returns>The index</returns>
    internal static LineIndex Open(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the IDs of the lines whose message matches an expression; every inserted row is deleted before the call returns or throws.
    /// </summary>
    /// <param name="lines">The lines of the chunk</param>
    /// <param name="match">The FTS5 expression</param>
    /// <param name="cancellationToken">Cancels the call</param>
    /// <returns>A task that returns the IDs of the matching lines</returns>
    internal Task<HashSet<long>> MatchAsync(IReadOnlyList<(long Id, string Message)> lines, string match, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    #endregion // IDisposable
}