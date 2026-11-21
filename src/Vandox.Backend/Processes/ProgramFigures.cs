using Vandox.Storage;

namespace Vandox.Backend.Processes;

#pragma warning disable RH2003 // Skeleton: removed with the implementation
/// <summary>
/// The programs of one process snapshot reduced to their figures.
/// </summary>
public sealed class ProgramFigures
{
    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the source.
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Gets whether the snapshot lists every program.
    /// </summary>
    public bool ListsEveryProgram { get; init; }

    /// <summary>
    /// Gets the figures by program name.
    /// </summary>
    public IReadOnlyDictionary<string, ProgramFigure> Programs { get; init; } = new Dictionary<string, ProgramFigure>();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Reduces a stored process snapshot to its figures.
    /// </summary>
    /// <param name="record">The stored record</param>
    /// <returns>The figures</returns>
    public static ProgramFigures FromRecord(StoredRecord record)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}

#pragma warning restore RH2003 // Skeleton: removed with the implementation