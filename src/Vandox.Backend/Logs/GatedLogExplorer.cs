using Vandox.Storage;

namespace Vandox.Backend.Logs;

#pragma warning disable RH2003, S2325

/// <summary>
/// Limits the explorer calls in flight across all callers.
/// </summary>
public sealed class GatedLogExplorer : ILogExplorer, IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="GatedLogExplorer"/> class.
    /// </summary>
    /// <param name="inner">The explorer to call</param>
    /// <param name="maxConcurrent">The number of slots, 1 to 3</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot count is not 1 to 3</exception>
    public GatedLogExplorer(ILogExplorer inner, int maxConcurrent)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region ILogExplorer

    /// <inheritdoc />
    public Task<LogPage> FindLogsAsync(LogFilter filter, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<LogContext> LogContextAsync(LogContextQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> LogSourcesAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<LogTemplate?> TemplateAsync(long id, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateCount>> TemplatesAsync(TemplateListQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DateTimeOffset>> SourceHoursAsync(SourceHourQuery query, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // ILogExplorer

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    #endregion // IDisposable
}