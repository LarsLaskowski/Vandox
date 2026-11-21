using Vandox.Core.Model;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="ProcessSnapshot"/>
/// </summary>
[TestClass]
public class ProcessSnapshotTests
{
    #region Methods

    /// <summary>
    /// A snapshot that names no program does not list every program, whatever it says about its completeness or total.
    /// </summary>
    /// <param name="listed">Whether the list of programs exists, empty</param>
    /// <param name="complete">The completeness flag</param>
    /// <param name="total">The total, <c>null</c> for none</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(false, true, null)]
    [DataRow(true, true, null)]
    [DataRow(true, true, 0u)]
    [DataRow(true, false, null)]
    public void ProcessSnapshotListsEveryProgramIsFalseWithoutPrograms(bool listed, bool complete, uint? total)
    {
        // Arrange
        var snapshot = Snapshot(complete, total, listed ? [] : null);

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsFalse(result, "no program is named");
    }

    /// <summary>
    /// A snapshot whose program counts add up to the total lists every program, even when it says it is not complete.
    /// </summary>
    /// <param name="complete">The completeness flag</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(true)]
    [DataRow(false)]
    public void ProcessSnapshotListsEveryProgramIsTrueWhenTheCountsEqualTheTotal(bool complete)
    {
        // Arrange
        var snapshot = Snapshot(complete, 5, Program("nginx", 3), Program("php", 2));

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsTrue(result, "the counts add up to the total");
    }

    /// <summary>
    /// A snapshot whose program counts differ from the total, below or above it, does not list every program, even when it says it is complete.
    /// </summary>
    /// <param name="total">The total</param>
    [TestMethod]
    [TestCategory("AC6")]
    [DataRow(4u)]
    [DataRow(6u)]
    public void ProcessSnapshotListsEveryProgramIsFalseWhenTheCountsDifferFromTheTotal(uint total)
    {
        // Arrange
        var snapshot = Snapshot(true, total, Program("nginx", 3), Program("php", 2));

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsFalse(result, "the counts do not add up to the total");
    }

    /// <summary>
    /// Without a total the snapshot lists every program when it says it is complete.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProcessSnapshotListsEveryProgramIsTrueWithoutATotalWhenComplete()
    {
        // Arrange
        var snapshot = Snapshot(true, null, Program("nginx", 3));

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsTrue(result, "no total, and the list says it is complete");
    }

    /// <summary>
    /// Without a total the snapshot does not list every program when it says it is not complete.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProcessSnapshotListsEveryProgramIsFalseWithoutATotalWhenNotComplete()
    {
        // Arrange
        var snapshot = Snapshot(false, null, Program("nginx", 3));

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsFalse(result, "no total, and the list says it is not complete");
    }

    /// <summary>
    /// The counts are summed without wrapping around: two counts whose sum passes the largest total do not match the total the wrapped sum equals.
    /// </summary>
    [TestMethod]
    [TestCategory("AC6")]
    public void ProcessSnapshotListsEveryProgramSumsTheCountsInLong()
    {
        // Arrange
        const uint wrapped = 1_705_032_704;
        var snapshot = Snapshot(true, wrapped, Program("nginx", 3_000_000_000), Program("php", 3_000_000_000));

        // Act
        var result = snapshot.ListsEveryProgram();

        // Assert
        Assert.IsFalse(result, "6,000,000,000 programs are not 1,705,032,704");
    }

    /// <summary>
    /// Creates a snapshot.
    /// </summary>
    /// <param name="complete">The completeness flag</param>
    /// <param name="total">The total</param>
    /// <param name="programs">The programs</param>
    /// <returns>The snapshot</returns>
    private static ProcessSnapshot Snapshot(bool complete, uint? total, params List<ProgramAggregate>? programs)
    {
        return new ProcessSnapshot
               {
                   Complete = complete,
                   Total = total,
                   Programs = programs
               };
    }

    /// <summary>
    /// Creates the sum of one program.
    /// </summary>
    /// <param name="name">The program</param>
    /// <param name="count">The number of processes</param>
    /// <returns>The sum</returns>
    private static ProgramAggregate Program(string name, uint count)
    {
        return new ProgramAggregate
               {
                   Program = name,
                   Count = count
               };
    }

    #endregion // Methods
}