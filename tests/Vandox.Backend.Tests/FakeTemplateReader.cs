using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="ILogTemplateReader"/> that answers only the hourly counts of a template with a scripted list. Its calls count in the counters of a
/// <see cref="FakeLogExplorer"/>.
/// </summary>
internal sealed class FakeTemplateReader : ILogTemplateReader
{
    #region Constants

    /// <summary>
    /// The name of <see cref="TemplateHoursAsync"/> in the calls of the explorer.
    /// </summary>
    internal const string TemplateHours = "TemplateHours";

    #endregion // Constants

    #region Fields

    private readonly FakeLogExplorer _explorer;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeTemplateReader"/> class.
    /// </summary>
    /// <param name="explorer">The explorer whose counters this reader shares</param>
    internal FakeTemplateReader(FakeLogExplorer explorer)
    {
        _explorer = explorer;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets or sets the result of <see cref="TemplateHoursAsync"/>.
    /// </summary>
    internal IReadOnlyList<TemplateHour> Hours { get; set; } = [];

    #endregion // Properties

    #region ILogTemplateReader

    /// <inheritdoc />
    public Task<LogTemplate?> FindTemplateAsync(string source, string program, string message, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateHour>> TemplateHoursAsync(TemplateHourQuery query, CancellationToken cancellationToken)
    {
        var copy = new TemplateHourQuery
                   {
                       TemplateId = query.TemplateId,
                       From = query.From,
                       To = query.To,
                       Limit = query.Limit
                   };

        return _explorer.RunAsync(TemplateHours, copy, () => Hours, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LogTemplate>> NewTemplatesAsync(NewTemplateQuery query, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TemplateSpike>> TemplateSpikesAsync(TemplateSpikeQuery query, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    #endregion // ILogTemplateReader
}