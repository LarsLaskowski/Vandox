using Microsoft.Data.Sqlite;

namespace Vandox.Storage;

/// <summary>
/// Sums the log lines of one write transaction per template and writes the sums.
/// </summary>
#pragma warning disable RH2003, S2325, S1172
internal sealed class TemplateTally : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TemplateTally"/> class.
    /// </summary>
    /// <param name="connection">The writer connection</param>
    /// <param name="transaction">The transaction</param>
    internal TemplateTally(SqliteConnection connection, SqliteTransaction transaction)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Counts a log line under its template.
    /// </summary>
    /// <param name="source">The source of the record</param>
    /// <param name="program">The program</param>
    /// <param name="message">The message</param>
    /// <param name="capturedAt">The capture time in Unix nanoseconds</param>
    internal void Add(string source, string program, string message, long capturedAt)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Writes the sums; nothing when no line was counted.
    /// </summary>
    /// <param name="cancellationToken">Cancels the write</param>
    internal void Write(CancellationToken cancellationToken)
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