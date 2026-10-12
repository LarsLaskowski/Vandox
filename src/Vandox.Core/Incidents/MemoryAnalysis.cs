using Vandox.Core.Model;

namespace Vandox.Core.Incidents;

/// <summary>
/// Reads the memory use of programs from process snapshots.
/// </summary>
public static class MemoryAnalysis
{
    #region Methods

    /// <summary>
    /// Normalizes a program name to its key.
    /// </summary>
    /// <param name="program">The program name</param>
    /// <returns>The key</returns>
    public static string ProgramKey(string program)
    {
        var slash = program.LastIndexOf('/');

        if (program.StartsWith('/') && slash < program.Length - 1)
        {
            return program[(slash + 1)..];
        }

        return program;
    }

    /// <summary>
    /// Returns the programs of a snapshot by key.
    /// </summary>
    /// <param name="snapshot">The snapshot</param>
    /// <returns>The programs</returns>
    public static IReadOnlyDictionary<string, ProgramUsage> Programs(ProcessSnapshot snapshot)
    {
        var programs = new Dictionary<string, ProgramUsage>(StringComparer.Ordinal);

        if (snapshot.Programs is { Count: > 0 })
        {
            foreach (var aggregate in snapshot.Programs)
            {
                Add(programs, aggregate.Program, aggregate.RssBytes, aggregate.Count);
            }
        }
        else
        {
            foreach (var process in snapshot.Processes ?? [])
            {
                var space = process.Command.IndexOf(' ', StringComparison.Ordinal);
                var word = space < 0 ? process.Command : process.Command[..space];

                Add(programs, word.Length == 0 ? process.Command : word, process.RssBytes, 1);
            }
        }

        return programs;
    }

    /// <summary>
    /// Ranks the programs whose RSS grew.
    /// </summary>
    /// <param name="first">The programs of the first snapshot</param>
    /// <param name="last">The programs of the last snapshot</param>
    /// <param name="count">The most candidates</param>
    /// <returns>The candidates</returns>
    public static IReadOnlyList<ProgramGrowth> Growth(IReadOnlyDictionary<string, ProgramUsage> first, IReadOnlyDictionary<string, ProgramUsage> last, int count)
    {
        return last.Values
                   .Select(usage => new ProgramGrowth
                                    {
                                        Program = usage.Program,
                                        StartRssBytes = first.TryGetValue(usage.Program, out var start) ? start.RssBytes : 0,
                                        EndRssBytes = usage.RssBytes
                                    })
                   .Where(growth => growth.EndRssBytes > growth.StartRssBytes)
                   .Select(growth =>
                           {
                               growth.GrowthBytes = growth.EndRssBytes - growth.StartRssBytes;

                               return growth;
                           })
                   .OrderByDescending(growth => growth.GrowthBytes)
                   .ThenBy(growth => growth.Program, StringComparer.Ordinal)
                   .Take(count)
                   .ToList();
    }

    /// <summary>
    /// Returns the programs with the largest RSS.
    /// </summary>
    /// <param name="programs">The programs</param>
    /// <param name="count">The most programs</param>
    /// <returns>The programs</returns>
    public static IReadOnlyList<ProgramUsage> Largest(IReadOnlyDictionary<string, ProgramUsage> programs, int count)
    {
        return programs.Values
                       .OrderByDescending(usage => usage.RssBytes)
                       .ThenBy(usage => usage.Program, StringComparer.Ordinal)
                       .Take(count)
                       .ToList();
    }

    /// <summary>
    /// Adds a program to the sums, saturating at the largest value.
    /// </summary>
    /// <param name="programs">The sums by key</param>
    /// <param name="program">The program name</param>
    /// <param name="rssBytes">The resident memory</param>
    /// <param name="count">The number of processes</param>
    private static void Add(Dictionary<string, ProgramUsage> programs, string program, ulong rssBytes, long count)
    {
        var key = ProgramKey(program);

        var usage = programs.GetValueOrDefault(key);

        if (usage is null)
        {
            usage = new ProgramUsage
                    {
                        Program = key
                    };
            programs[key] = usage;
        }

        usage.RssBytes = ulong.MaxValue - usage.RssBytes < rssBytes ? ulong.MaxValue : usage.RssBytes + rssBytes;
        usage.Count += count;
    }

    #endregion // Methods
}