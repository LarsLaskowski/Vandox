using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The threads of one MariaDB status record.
/// </summary>
public sealed class ThreadSnapshot
{
    #region Constants

    /// <summary>
    /// The most thread rows of a snapshot.
    /// </summary>
    public const int MaxRows = 101;

    /// <summary>
    /// The largest seconds value of a stamp: 100 years.
    /// </summary>
    public const long MaxSeconds = 3153600000;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets the capture time.
    /// </summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// Gets the number of threads.
    /// </summary>
    public int Threads { get; init; }

    /// <summary>
    /// Gets the number of threads that are not sleeping.
    /// </summary>
    public int Running { get; init; }

    /// <summary>
    /// Gets the number of sleeping threads.
    /// </summary>
    public int Sleeping { get; init; }

    /// <summary>
    /// Gets the threads that are not sleeping, in the order of the agent, at most <see cref="MaxRows"/>.
    /// </summary>
    public IReadOnlyList<MariaDbThread> Rows { get; init; } = [];

    #endregion // Properties
}