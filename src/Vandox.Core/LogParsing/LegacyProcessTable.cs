using Vandox.Core.Model;

namespace Vandox.Core.LogParsing;

/// <summary>
/// The rows of the process table of one snapshot.
/// </summary>
internal sealed class LegacyProcessTable
{
    #region Constants

    private const int MaxCmdlineBytes = 2048;

    #endregion // Constants

    #region Fields

    private readonly List<LegacyTopRow> _rows = [];
    private readonly List<bool> _cut = [];
    private readonly HashSet<int> _pids = [];

    #endregion // Fields

    #region Properties

    /// <summary>
    /// Gets the number of rows kept.
    /// </summary>
    internal int Count => _rows.Count;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Adds a row unless an earlier row has its process id.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="lineCut">Whether the line of the row was cut by the reader</param>
    /// <returns><c>false</c> for a duplicate process id</returns>
    internal bool TryAdd(LegacyTopRow row, bool lineCut)
    {
        if (_pids.Add(row.Pid))
        {
            var command = Utf8Text.Cut(row.Command, MaxCmdlineBytes, out var cut);

            _rows.Add(row with
                          {
                              Command = command
                          });
            _cut.Add(cut || lineCut);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds the process snapshot.
    /// </summary>
    /// <param name="total">The number of tasks of the Tasks line</param>
    /// <param name="newFormat">Whether the snapshot has sections, so the command line is the whole COMMAND column</param>
    /// <param name="complete">Whether every row was read</param>
    /// <returns>The snapshot; <c>null</c> without a row</returns>
    internal ProcessSnapshot? Build(uint total, bool newFormat, bool complete)
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        var processes = new List<ProcessSample>(_rows.Count);
        var programs = new List<ProgramAggregate>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var position = 0; position < _rows.Count; position++)
        {
            var sample = Sample(_rows[position], _cut[position], newFormat);

            processes.Add(sample);
            Aggregate(programs, index, sample);
        }

        return new ProcessSnapshot
               {
                   Complete = complete,
                   Total = total,
                   Processes = processes,
                   Programs = programs
               };
    }

    /// <summary>
    /// Creates the sample of a row.
    /// </summary>
    /// <param name="row">The row</param>
    /// <param name="cut">Whether the command column was cut</param>
    /// <param name="newFormat">Whether the snapshot has sections</param>
    /// <returns>The sample</returns>
    private static ProcessSample Sample(LegacyTopRow row, bool cut, bool newFormat)
    {
        var sample = new ProcessSample
                     {
                         Pid = row.Pid,
                         User = row.User,
                         State = row.State,
                         CpuPercent = row.CpuPercent,
                         RssBytes = row.ResBytes
                     };

        if (newFormat)
        {
            var commandLine = LegacyProcessNames.Normalize(row.Command, out var plus);

            sample.Cmdline = commandLine;
            sample.Command = LegacyProcessNames.CommandOf(commandLine, row.Command);
            sample.Truncated = plus || cut;
        }
        else
        {
            sample.Command = LegacyProcessNames.OldCommandOf(row.Command, out var truncated);
            sample.Truncated = truncated || cut;
        }

        return sample;
    }

    /// <summary>
    /// Adds a sample to the sums of its program.
    /// </summary>
    /// <param name="programs">The programs in order of first appearance</param>
    /// <param name="index">The position of each program</param>
    /// <param name="sample">The sample</param>
    private static void Aggregate(List<ProgramAggregate> programs, Dictionary<string, int> index, ProcessSample sample)
    {
        var name = LegacyProcessNames.ProgramOf(sample.Command);

        var program = programs[index.TryGetValue(name, out var found) ? found : AddProgram(programs, index, name)];

        program.Count++;
        program.CpuPercent += sample.CpuPercent;
        program.RssBytes = ulong.MaxValue - program.RssBytes < sample.RssBytes ? ulong.MaxValue : program.RssBytes + sample.RssBytes;
        program.RssOvercounted = program.Count > 1;
    }

    /// <summary>
    /// Adds an empty program.
    /// </summary>
    /// <param name="programs">The programs in order of first appearance</param>
    /// <param name="index">The position of each program</param>
    /// <param name="name">The program name</param>
    /// <returns>The position of the new program</returns>
    private static int AddProgram(List<ProgramAggregate> programs, Dictionary<string, int> index, string name)
    {
        index[name] = programs.Count;
        programs.Add(new ProgramAggregate
                     {
                         Program = name
                     });

        return programs.Count - 1;
    }

    #endregion // Methods
}