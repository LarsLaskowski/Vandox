using Vandox.Backend.Processes;
using Vandox.Core.Model;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="ProgramFigures"/>
/// </summary>
[TestClass]
public class ProgramFiguresTests
{
    #region Methods

    /// <summary>
    /// The figures take the source, the capture time and the per-program values of the snapshot.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramFiguresFromRecordTakesTheValuesOfTheSnapshot()
    {
        // Arrange
        var at = HistorySamples.Base.AddMinutes(5);
        var record = ProcessesSamples.Snapshot(1, "agent", at, ("a", 4096, 1.5, 2048), ("b", 8192, 0.5, null));

        // Act
        var figures = ProgramFigures.FromRecord(record);

        // Assert
        Assert.AreEqual("agent", figures.Source, "the source of the record");
        Assert.AreEqual(at, figures.CapturedAt, "the capture time of the record");
        Assert.IsTrue(figures.ListsEveryProgram, "a complete snapshot lists every program");
        Assert.HasCount(2, figures.Programs, "one figure per program");
        Assert.AreEqual(new ProgramFigure(4096, 2048, 1.5), figures.Programs["a"], "the figure with a proportional size");
        Assert.AreEqual(new ProgramFigure(8192, null, 0.5), figures.Programs["b"], "the figure without one");
    }

    /// <summary>
    /// A snapshot without a program list gives no figure.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramFiguresFromRecordOfASnapshotWithoutProgramsHasNone()
    {
        // Arrange
        var record = HistorySamples.Record(1,
                                           "agent",
                                           HistorySamples.Base,
                                           new ProcessSnapshot
                                           {
                                               Complete = true,
                                               Programs = null
                                           });

        // Act
        var figures = ProgramFigures.FromRecord(record);

        // Assert
        Assert.IsEmpty(figures.Programs, "no programs");
    }

    /// <summary>
    /// A record that is no process snapshot is refused, and so is no record at all.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProgramFiguresFromRecordRefusesWhatIsNoProcessSnapshot()
    {
        // Arrange
        var record = HistorySamples.Record(1, "agent", HistorySamples.Base, new Gap());

        // Act
        var wrongKind = Assert.Throws<ArgumentException>(() => ProgramFigures.FromRecord(record), "a gap is no snapshot");
        var missing = Assert.Throws<ArgumentNullException>(() => ProgramFigures.FromRecord(null!), "no record");

        // Assert
        Assert.AreEqual("record", wrongKind.ParamName, "the parameter of the refusal");
        Assert.AreEqual("record", missing.ParamName, "the parameter of the null check");
    }

    #endregion // Methods
}