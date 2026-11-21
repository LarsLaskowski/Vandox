using Vandox.Backend.Logs;
using Vandox.Backend.Processes;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="GatedProgramRollupReader"/>
/// </summary>
[TestClass]
public class GatedProgramRollupReaderTests
{
    #region Fields

    private static readonly ProgramRollupQuery _rollupQuery = new()
                                                              {
                                                                  Source = "agent",
                                                                  From = HistorySamples.Base,
                                                                  To = HistorySamples.Base.AddHours(1),
                                                                  Step = TimeSpan.FromMinutes(5),
                                                                  Measure = ProgramMeasure.Memory,
                                                                  Count = 10
                                                              };

    private static readonly ProgramSourceQuery _sourceQuery = new()
                                                              {
                                                                  From = HistorySamples.Base,
                                                                  To = HistorySamples.Base.AddHours(1)
                                                              };

    private static readonly ProgramRollupResult _rollups = ProcessesSamples.Result(HistorySamples.Base, ProgramMemoryKey.Pss, "nginx");

    private static readonly ProgramSourceList _sources = ProcessesSamples.SourceList(false, ("agent", HistorySamples.Base));

    private static readonly string[] _memberNames = [
                                                        nameof(IProgramRollupReader.ProgramRollupsAsync),
                                                        nameof(IProgramRollupReader.ProgramSourcesAsync)
                                                    ];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Each of the two calls takes one slot of the gate while it runs, so a second call waits; each passes its query and token to the inner reader, returns its answer and frees the slot.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [TestCategory("AC9")]
    public async Task GatedProgramRollupReaderTakesASlotPerCall()
    {
        // Arrange
        var problems = new List<string>();

        // Act
        for (var member = 0; member < _memberNames.Length; member++)
        {
            await CheckMemberAsync(member, problems);
        }

        // Assert
        Assert.IsEmpty(problems, string.Join("; ", problems));
    }

    /// <summary>
    /// Adds a problem when a check is violated.
    /// </summary>
    /// <param name="problems">The problems</param>
    /// <param name="violated">Whether the check is violated</param>
    /// <param name="text">The problem</param>
    private static void AddProblem(List<string> problems, bool violated, string text)
    {
        if (violated)
        {
            problems.Add(text);
        }
    }

    /// <summary>
    /// Calls a member of the gated reader.
    /// </summary>
    /// <param name="reader">The gated reader</param>
    /// <param name="member">The index of the member</param>
    /// <param name="cancellationToken">The token of the call</param>
    /// <returns>A task that returns the answer</returns>
    private static async Task<object?> Call(GatedProgramRollupReader reader, int member, CancellationToken cancellationToken)
    {
        return member switch
               {
                   0 => await reader.ProgramRollupsAsync(_rollupQuery, cancellationToken),
                   _ => await reader.ProgramSourcesAsync(_sourceQuery, cancellationToken)
               };
    }

    /// <summary>
    /// Gets the argument the inner reader records for a member.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <returns>The argument</returns>
    private static object Argument(int member)
    {
        return member switch
               {
                   0 => _rollupQuery,
                   _ => _sourceQuery
               };
    }

    /// <summary>
    /// Gets the answer the inner reader gives for a member.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <returns>The answer</returns>
    private static object Answer(int member)
    {
        return member switch
               {
                   0 => _rollups,
                   _ => _sources
               };
    }

    /// <summary>
    /// Creates an inner reader with distinct answers that holds its calls until a task completes.
    /// </summary>
    /// <param name="hold">The task the calls wait for</param>
    /// <returns>The inner reader</returns>
    private static FakeProgramRollupReader HeldReader(Task hold)
    {
        var reader = new FakeProgramRollupReader
                     {
                         Hold = hold,
                         Sources = _sources
                     };

        reader.Results[ProgramMeasure.Memory] = _rollups;

        return reader;
    }

    /// <summary>
    /// Checks one member: a held first call takes the only slot, a second call waits, both reach the inner reader with their argument and token, return its answer, and the slot is free afterwards.
    /// </summary>
    /// <param name="member">The index of the member</param>
    /// <param name="problems">Receives the problems found</param>
    /// <returns>A task that completes when the member is checked</returns>
    private async Task CheckMemberAsync(int member, List<string> problems)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var inner = HeldReader(release.Task);
        using var gate = new WebReaderGate(1);
        using var secondSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        var reader = new GatedProgramRollupReader(inner, gate);
        var first = Call(reader, member, TestContext.CancellationToken);
        var entry = inner.WaitForEntryAsync(TestContext.CancellationToken);

        await Task.WhenAny(entry, first);

        var entered = entry.IsCompletedSuccessfully && entry.Result;
        var second = Call(reader, member, secondSource.Token);
        var whileHeld = inner.Calls.Count;

        release.SetResult();

        var firstAnswer = await first.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var secondAnswer = await second.WaitAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken);
        var free = await gate.RunAsync(() => Task.FromResult(true), TestContext.CancellationToken);
        var name = _memberNames[member];
        var calls = inner.Calls;

        AddProblem(problems, entered == false, $"{name}: the first call never reached the inner reader");
        AddProblem(problems, whileHeld != 1, $"{name}: the inner reader sees {whileHeld} calls while the slot is taken, not 1");
        AddProblem(problems, inner.MaxInFlight != 1, $"{name}: {inner.MaxInFlight} calls in flight at once");
        AddProblem(problems, calls.Count != 2, $"{name}: {calls.Count} calls reached the inner reader, not 2");
        AddProblem(problems, calls.Any(call => call.Name != name), $"{name}: a call reached another member of the inner reader");
        AddProblem(problems, calls.Any(call => Equals(call.Argument, Argument(member)) == false), $"{name}: the argument does not reach the inner reader");
        AddProblem(problems, calls.Count == 2 && calls[1].Token != secondSource.Token, $"{name}: the token of the call does not reach the inner reader");
        AddProblem(problems, ReferenceEquals(firstAnswer, Answer(member)) == false || ReferenceEquals(secondAnswer, Answer(member)) == false, $"{name}: the answer of the inner reader does not reach the caller");
        AddProblem(problems, free == false, $"{name}: the slot is not free after both calls ended");
    }

    #endregion // Methods
}