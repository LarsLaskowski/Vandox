using Vandox.Backend.Processes;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Builds the rollup results, source lists and snapshots the processes view tests read.
/// </summary>
internal static class ProcessesSamples
{
    #region Constants

    /// <summary>
    /// The bytes of one MiB.
    /// </summary>
    internal const double Mebibyte = 1048576.0;

    #endregion // Constants

    #region Methods

    /// <summary>
    /// Creates the step starts of a result.
    /// </summary>
    /// <param name="start">The first step start</param>
    /// <param name="step">The step</param>
    /// <param name="count">The number of steps</param>
    /// <returns>The step starts</returns>
    internal static IReadOnlyList<DateTimeOffset> Times(DateTimeOffset start, TimeSpan step, int count)
    {
        return [.. Enumerable.Range(0, count).Select(index => start + (step * index))];
    }

    /// <summary>
    /// Creates a point whose average, minimum and maximum are given.
    /// </summary>
    /// <param name="average">The average</param>
    /// <param name="minimum">The minimum</param>
    /// <param name="maximum">The maximum</param>
    /// <returns>The point</returns>
    internal static ProgramRollupPoint Point(double average, double minimum, double maximum)
    {
        return new ProgramRollupPoint(average, minimum, maximum);
    }

    /// <summary>
    /// Creates a line of a program.
    /// </summary>
    /// <param name="program">The program</param>
    /// <param name="points">The points, <c>null</c> for a gap</param>
    /// <returns>The line</returns>
    internal static ProgramRollupLine Line(string program, params ProgramRollupPoint?[] points)
    {
        return new ProgramRollupLine
               {
                   Program = program,
                   Points = points
               };
    }

    /// <summary>
    /// Creates a result of two steps with the given programs, every point the same in both steps.
    /// </summary>
    /// <param name="start">The first step start</param>
    /// <param name="key">The memory key, <c>null</c> for a CPU result</param>
    /// <param name="programs">The names of the programs</param>
    /// <returns>The result</returns>
    internal static ProgramRollupResult Result(DateTimeOffset start, ProgramMemoryKey? key, params string[] programs)
    {
        return new ProgramRollupResult
               {
                   Times = Times(start, TimeSpan.FromMinutes(10), 2),
                   Programs = [.. programs.Select(program => Line(program, Point(Mebibyte, Mebibyte / 2, Mebibyte * 2), Point(Mebibyte, Mebibyte / 2, Mebibyte * 2)))],
                   Others = [Mebibyte, Mebibyte],
                   Incomplete = [0, 0],
                   MemoryKey = key
               };
    }

    /// <summary>
    /// Creates a list of sources.
    /// </summary>
    /// <param name="more">Whether the list is cut</param>
    /// <param name="sources">The sources with the time of their latest bucket</param>
    /// <returns>The list</returns>
    internal static ProgramSourceList SourceList(bool more, params (string Source, DateTimeOffset Latest)[] sources)
    {
        return new ProgramSourceList
               {
                   Sources = [
                                 .. sources.Select(source => new ProgramSource
                                                             {
                                                                 Source = source.Source,
                                                                 LatestBucket = source.Latest
                                                             })
                             ],
                   More = more
               };
    }

    /// <summary>
    /// Creates the figures of a program.
    /// </summary>
    /// <param name="rss">The resident bytes</param>
    /// <param name="pss">The proportional bytes, <c>null</c> for none</param>
    /// <param name="cpu">The CPU percent</param>
    /// <returns>The figures</returns>
    internal static ProgramFigure Figure(double rss, double? pss, double cpu)
    {
        return new ProgramFigure(rss, pss, cpu);
    }

    /// <summary>
    /// Creates the figures of a snapshot.
    /// </summary>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="listsEveryProgram">Whether the snapshot lists every program</param>
    /// <param name="programs">The programs by name</param>
    /// <returns>The figures</returns>
    internal static ProgramFigures Figures(DateTimeOffset capturedAt, bool listsEveryProgram, params (string Program, ProgramFigure Figure)[] programs)
    {
        return new ProgramFigures
               {
                   CapturedAt = capturedAt,
                   Source = "agent",
                   ListsEveryProgram = listsEveryProgram,
                   Programs = programs.ToDictionary(program => program.Program, program => program.Figure, StringComparer.Ordinal)
               };
    }

    /// <summary>
    /// Creates a stored process snapshot that lists the given programs and says it is complete.
    /// </summary>
    /// <param name="id">The record ID</param>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="programs">The programs: name, resident bytes and CPU percent, with proportional bytes when given</param>
    /// <returns>The stored record</returns>
    internal static StoredRecord Snapshot(long id, string source, DateTimeOffset capturedAt, params (string Program, ulong Rss, double Cpu, ulong? Pss)[] programs)
    {
        return HistorySamples.Record(id,
                                     source,
                                     capturedAt,
                                     new ProcessSnapshot
                                     {
                                         Complete = true,
                                         Programs = [
                                                        .. programs.Select(program => new ProgramAggregate
                                                                                      {
                                                                                          Program = program.Program,
                                                                                          Count = 1,
                                                                                          CpuPercent = program.Cpu,
                                                                                          RssBytes = program.Rss,
                                                                                          PssBytes = program.Pss
                                                                                      })
                                                    ]
                                     });
    }

    #endregion // Methods
}