using Vandox.Backend.Logs;

namespace Vandox.Backend.Incidents;

/// <summary>
/// A program with an amount of memory, as the pages show it.
/// </summary>
public sealed class ProgramAmountView
{
    #region Properties

    /// <summary>
    /// Gets the program name.
    /// </summary>
    public IReadOnlyList<TextRun> Program { get; init; } = [];

    /// <summary>
    /// Gets the amount as text.
    /// </summary>
    public string Amount { get; init; } = string.Empty;

    #endregion // Properties
}