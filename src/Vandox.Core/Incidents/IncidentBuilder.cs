using Vandox.Core.Model;
using Vandox.Core.Signatures;

namespace Vandox.Core.Incidents;

#pragma warning disable RH2003, S2325

/// <summary>
/// Builds the incident of a kill cluster from the records of its window.
/// </summary>
public sealed class IncidentBuilder
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="IncidentBuilder"/> class.
    /// </summary>
    /// <param name="cluster">The kill cluster</param>
    /// <param name="before">The window before a kill</param>
    /// <param name="after">The window after a kill</param>
    public IncidentBuilder(KillCluster cluster, TimeSpan before, TimeSpan after)
    {
        throw new NotImplementedException();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Adds a detected event of the window.
    /// </summary>
    /// <param name="detected">The event</param>
    /// <exception cref="InvalidOperationException">A process snapshot was added before</exception>
    public void AddEvent(DetectedEvent detected)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a process snapshot of the window.
    /// </summary>
    /// <param name="recordId">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    public void AddProcessSnapshot(long recordId, DateTimeOffset capturedAt, ProcessSnapshot snapshot)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a connection snapshot of the window.
    /// </summary>
    /// <param name="recordId">The record ID</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="snapshot">The snapshot</param>
    public void AddConnectionSnapshot(long recordId, DateTimeOffset capturedAt, ConnectionSnapshot snapshot)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a metric sample of the window.
    /// </summary>
    /// <param name="source">The source</param>
    /// <param name="capturedAt">The capture time</param>
    /// <param name="metric">The metric</param>
    public void AddMetric(string source, DateTimeOffset capturedAt, MetricPoint metric)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a new log template of the window.
    /// </summary>
    /// <param name="template">The template</param>
    public void AddNewTemplate(IncidentNewTemplate template)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Adds a template spike of the window.
    /// </summary>
    /// <param name="spike">The spike</param>
    public void AddTemplateSpike(IncidentTemplateSpike spike)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Builds the incident.
    /// </summary>
    /// <returns>The incident</returns>
    /// <exception cref="InvalidOperationException">The incident was built before</exception>
    public Incident Build()
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}