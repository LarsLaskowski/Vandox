using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// A template reader that passes every query to another reader and records the queries for new templates and spikes.
/// </summary>
internal sealed class RecordingTemplateReader : ILogTemplateReader
{
    #region Fields

    private readonly ILogTemplateReader _inner;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingTemplateReader"/> class.
    /// </summary>
    /// <param name="inner">The reader that answers the queries</param>
    internal RecordingTemplateReader(ILogTemplateReader inner)
    {
        _inner = inner;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a copy of every query for new templates.
    /// </summary>
    internal List<NewTemplateQuery> NewTemplateQueries { get; } = [];

    /// <summary>
    /// Gets a copy of every query for spikes.
    /// </summary>
    internal List<TemplateSpikeQuery> SpikeQueries { get; } = [];

    #endregion // Properties

    #region ILogTemplateReader

    /// <inheritdoc />
    public Task<LogTemplate?> FindTemplateAsync(string source, string program, string message, CancellationToken cancellationToken)
    {
        return _inner.FindTemplateAsync(source, program, message, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken)
    {
        return _inner.TemplateHoursAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LogTemplate>> NewTemplatesAsync(NewTemplateQuery query, CancellationToken cancellationToken)
    {
        NewTemplateQueries.Add(new NewTemplateQuery
                               {
                                   Source = query.Source,
                                   From = query.From,
                                   To = query.To,
                                   Limit = query.Limit
                               });

        return _inner.NewTemplatesAsync(query, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateSpike>> TemplateSpikesAsync(TemplateSpikeQuery query, CancellationToken cancellationToken)
    {
        SpikeQueries.Add(new TemplateSpikeQuery
                         {
                             Source = query.Source,
                             From = query.From,
                             To = query.To,
                             BaselineHours = query.BaselineHours,
                             MinBaselineHours = query.MinBaselineHours,
                             Factor = query.Factor,
                             MinCount = query.MinCount,
                             Limit = query.Limit
                         });

        return _inner.TemplateSpikesAsync(query, cancellationToken);
    }

    #endregion // ILogTemplateReader
}