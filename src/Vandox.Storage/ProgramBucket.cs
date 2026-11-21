namespace Vandox.Storage;

/// <summary>
/// What one fold step adds to the bucket of a source: the total of its snapshots and the programs in the order the fold met them.
/// </summary>
internal sealed class ProgramBucket
{
    #region Fields

    private readonly Dictionary<string, ProgramBucketTotals> _programs = [with(StringComparer.Ordinal)];
    private readonly List<string> _order = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the total of the snapshots.
    /// </summary>
    internal ProgramSourceTotals Totals { get; } = new();

    /// <summary>
    /// Gets the programs in the order the fold met them.
    /// </summary>
    internal IEnumerable<KeyValuePair<string, ProgramBucketTotals>> Programs => _order.Select(name => new KeyValuePair<string, ProgramBucketTotals>(name, _programs[name]));

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds the values of a program in one snapshot.
    /// </summary>
    /// <param name="program">The program name</param>
    /// <param name="rss">The resident memory</param>
    /// <param name="pss">The proportional memory, or <c>null</c> when the snapshot has none</param>
    /// <param name="cpu">The CPU percentage</param>
    internal void Add(string program, double rss, double? pss, double cpu)
    {
        if (_programs.TryGetValue(program, out var totals) == false)
        {
            totals = new ProgramBucketTotals();
            _programs[program] = totals;
            _order.Add(program);
        }

        totals.Add(rss, pss, cpu);
    }

    #endregion // Methods
}