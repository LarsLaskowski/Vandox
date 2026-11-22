using Vandox.Backend.Logs;
using Vandox.Storage;

namespace Vandox.Backend.Processes;

/// <summary>
/// Reads the per-program rollups through the shared gate of the web views, one gate slot per call.
/// </summary>
public sealed class GatedProgramRollupReader : IProgramRollupReader
{
    #region Fields

    /// <summary>
    /// The reader to call.
    /// </summary>
    private readonly IProgramRollupReader _inner;

    /// <summary>
    /// The gate of the reads.
    /// </summary>
    private readonly WebReaderGate _gate;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedProgramRollupReader"/> class.
    /// </summary>
    /// <param name="inner">The reader to call</param>
    /// <param name="gate">The gate of the reads</param>
    public GatedProgramRollupReader(IProgramRollupReader inner, WebReaderGate gate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(gate);

        _inner = inner;
        _gate = gate;
    }

    #endregion // Constructors

    #region IProgramRollupReader

    /// <inheritdoc/>
    public Task<ProgramRollupResult> ProgramRollupsAsync(ProgramRollupQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ProgramRollupsAsync(query, cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ProgramSourceList> ProgramSourcesAsync(ProgramSourceQuery query, CancellationToken cancellationToken)
    {
        return _gate.RunAsync(() => _inner.ProgramSourcesAsync(query, cancellationToken), cancellationToken);
    }

    #endregion // IProgramRollupReader
}