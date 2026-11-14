using Vandox.Backend.Retention;
using Vandox.Core.Configuration;

namespace Vandox.Backend.Reports;

/// <summary>
/// The periods after which the report skips agent data; a null period is unlimited.
/// </summary>
public sealed class ReportRetention
{
    #region Methods

    /// <summary>
    /// Creates the periods of a configuration.
    /// </summary>
    /// <param name="config">The backend configuration</param>
    /// <returns>The periods</returns>
    internal static ReportRetention From(BackendConfig config)
    {
        var policy = RetentionService.CreatePolicy(config);

        return new ReportRetention
               {
                   Logs = policy.Logs,
                   Snapshots = policy.Snapshots
               };
    }

    #endregion // Methods

    #region Properties

    /// <summary>
    /// Gets how long log lines are kept, or <c>null</c> for unlimited.
    /// </summary>
    public TimeSpan? Logs { get; init; }

    /// <summary>
    /// Gets how long process snapshots are kept, or <c>null</c> for unlimited.
    /// </summary>
    public TimeSpan? Snapshots { get; init; }

    #endregion // Properties
}