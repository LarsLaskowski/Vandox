using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A record reader that passes every query to another reader and records the query and the IDs that came back.
/// </summary>
internal sealed class RecordingRecordReader : IRecordReader
{
    #region Fields

    private readonly IRecordReader _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingRecordReader"/> class.
    /// </summary>
    /// <param name="inner">The reader that answers the queries</param>
    internal RecordingRecordReader(IRecordReader inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a copy of every query in the order it was asked.
    /// </summary>
    internal List<RecordQuery> Queries { get; } = [];

    /// <summary>
    /// Gets the kind of the query and the ID of every record that was returned, in the order it was returned.
    /// </summary>
    internal List<(string Kind, long Id)> Returned { get; } = [];

    #endregion // Properties

    #region IRecordReader

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredRecord>> RecordsAsync(RecordQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(new RecordQuery
                    {
                        Kind = query.Kind,
                        Source = query.Source,
                        Name = query.Name,
                        From = query.From,
                        To = query.To,
                        Limit = query.Limit,
                        AfterId = query.AfterId
                    });

        var rows = await _inner.RecordsAsync(query, cancellationToken);

        Returned.AddRange(rows.Select(row => (query.Kind, row.Id)));

        return rows;
    }

    #endregion // IRecordReader
}