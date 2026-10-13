using Vandox.Core.Incidents;
using Vandox.Core.Reports;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ConsumerTracker"/>
/// </summary>
[TestClass]
public class ConsumerTrackerTests
{
    #region Constants

    private static readonly DateTimeOffset Start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    #endregion // Constants

    #region Methods

    /// <summary>
    /// A program in nine of ten snapshots is permanent, one in eight of ten is transient.
    /// </summary>
    [TestMethod]
    public void ConsumerTrackerSplitsPermanentAndTransient()
    {
        // Arrange
        var tracker = new ConsumerTracker();

        for (var index = 0; index < 10; index++)
        {
            var programs = new List<(string, ulong)> { ("always", 10) };

            if (index != 3)
            {
                programs.Add(("nine", 20));
            }

            if (index is not (2 or 5))
            {
                programs.Add(("eight", 30));
            }

            tracker.Add(Start.AddMinutes(index), Usage(programs.ToArray()));
        }

        // Act
        var permanent = tracker.Permanent(5);
        var transient = tracker.Transient(5);

        // Assert
        Assert.AreEqual(10, tracker.Snapshots, "all snapshots are counted");
        Assert.AreSequenceEqual(["nine", "always"], permanent.Select(consumer => consumer.Program).ToList(), "9 of 10 is exactly 90 %: permanent, ranked by mean");
        Assert.AreSequenceEqual(["eight"], transient.Select(consumer => consumer.Program).ToList(), "8 of 10 is transient");
        Assert.AreEqual(9, permanent[0].Snapshots, "snapshots that hold the program");
        Assert.AreEqual(0.9, permanent[0].Share, 1e-12, "share of the snapshots");
        Assert.AreEqual(0.8, transient[0].Share, 1e-12, "share of the snapshots of the transient program");
        Assert.AreEqual(0L, tracker.UntrackedEntries, "nothing untracked");
    }

    /// <summary>
    /// Permanent programs are ranked by mean RSS rounded down, transient ones by peak RSS with the first time of the peak; ties go by name.
    /// </summary>
    [TestMethod]
    public void ConsumerTrackerRanksByMeanAndPeak()
    {
        // Arrange
        var tracker = new ConsumerTracker();

        for (var index = 0; index < 10; index++)
        {
            var programs = new List<(string, ulong)>
                           {
                               ("big", 300),
                               ("tie-b", 200),
                               ("tie-a", 200),
                               ("odd", index == 0 ? 2UL : 1UL)
                           };

            switch (index)
            {
                case 3:
                    {
                        programs.Add(("peak", 500));
                        programs.Add(("x1", 90));
                        programs.Add(("x2", 80));
                    }
                    break;
                case 4:
                    {
                        programs.Add(("peak", 500));
                        programs.Add(("x3", 70));
                        programs.Add(("x4", 70));
                    }
                    break;
                case 5:
                    {
                        programs.Add(("peak", 400));
                        programs.Add(("x5", 60));
                        programs.Add(("x6", 50));
                    }
                    break;
            }

            tracker.Add(Start.AddMinutes(index), Usage(programs.ToArray()));
        }

        // Act
        var permanent = tracker.Permanent(10);
        var limited = tracker.Permanent(2);
        var transient = tracker.Transient(5);

        // Assert
        Assert.AreSequenceEqual(["big", "tie-a", "tie-b", "odd"], permanent.Select(consumer => consumer.Program).ToList(), "by mean, then by name");
        Assert.AreSequenceEqual(["big", "tie-a"], limited.Select(consumer => consumer.Program).ToList(), "the count limits the list");
        Assert.AreEqual(1UL, permanent[3].MeanRssBytes, "the mean 11 / 10 is rounded down");
        Assert.AreEqual(2UL, permanent[3].PeakRssBytes, "peak of the permanent program");
        Assert.AreSequenceEqual(["peak", "x1", "x2", "x3", "x4"], transient.Select(consumer => consumer.Program).ToList(), "by peak, then by name, at most the count");
        Assert.AreEqual(500UL, transient[0].PeakRssBytes, "peak RSS");
        Assert.AreEqual(Start.AddMinutes(3), transient[0].PeakAt, "the first snapshot that reached the peak");
        Assert.AreEqual(3, transient[0].Snapshots, "three snapshots hold the program");
        Assert.AreEqual(466UL, transient[0].MeanRssBytes, "the mean 1400 / 3 is rounded down");
    }

    /// <summary>
    /// A path and its last part are one program.
    /// </summary>
    [TestMethod]
    public void ConsumerTrackerJoinsProgramPaths()
    {
        // Arrange
        var tracker = new ConsumerTracker();

        tracker.Add(Start, Usage(("/usr/sbin/mariadbd", 100)));
        tracker.Add(Start.AddMinutes(1), Usage(("mariadbd", 300)));

        // Act
        var permanent = tracker.Permanent(5);

        // Assert
        Assert.HasCount(1, permanent, "one program");
        Assert.AreEqual("mariadbd", permanent[0].Program, "the last part of the path");
        Assert.AreEqual(2, permanent[0].Snapshots, "both snapshots");
        Assert.AreEqual(200UL, permanent[0].MeanRssBytes, "mean over both");
        Assert.AreEqual(300UL, permanent[0].PeakRssBytes, "peak over both");
    }

    /// <summary>
    /// Programs beyond the cap are not tracked and their entries are counted.
    /// </summary>
    [TestMethod]
    public void ConsumerTrackerStopsTrackingAtMaxPrograms()
    {
        // Arrange
        var tracker = new ConsumerTracker(2);

        tracker.Add(Start, Usage(("a", 10), ("b", 20)));
        tracker.Add(Start.AddMinutes(1), Usage(("a", 10), ("b", 20), ("c", 30)));
        tracker.Add(Start.AddMinutes(2), Usage(("c", 30)));

        // Act
        var names = tracker.Permanent(10).Concat(tracker.Transient(10)).Select(consumer => consumer.Program).Order(StringComparer.Ordinal).ToList();

        // Assert
        Assert.AreSequenceEqual(["a", "b"], names, "the third program is not tracked");
        Assert.AreEqual(2L, tracker.UntrackedEntries, "two entries of the untracked program");
        Assert.AreEqual(3, tracker.Snapshots, "all snapshots are counted");
    }

    /// <summary>
    /// Builds the programs of a snapshot by name, without reducing the names.
    /// </summary>
    /// <param name="programs">The programs with their resident memory in bytes</param>
    /// <returns>The programs</returns>
    private static Dictionary<string, ProgramUsage> Usage(params (string Program, ulong Rss)[] programs)
    {
        return programs.ToDictionary(entry => entry.Program, entry => new ProgramUsage { Program = entry.Program, RssBytes = entry.Rss, Count = 1 }, StringComparer.Ordinal);
    }

    #endregion // Methods
}
