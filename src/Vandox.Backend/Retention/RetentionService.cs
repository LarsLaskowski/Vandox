using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Retention;

/// <summary>
/// The background job that folds metric values into rollups and removes expired data in bounded steps.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    #region Fields

    // Skeleton: removed with the implementation
#pragma warning disable S4487
    private readonly IRetentionStore _store;
    private readonly RetentionPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
#pragma warning restore S4487

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="RetentionService"/> class.
    /// </summary>
    /// <param name="store">The retention store</param>
    /// <param name="policy">The retention policy</param>
    /// <param name="clock">The clock</param>
    /// <param name="logger">The logger</param>
    public RetentionService(IRetentionStore store, RetentionPolicy policy, TimeProvider clock, ILogger logger)
    {
        _store = store;
        _policy = policy;
        _clock = clock;
        _logger = logger;
    }

    #endregion // Constructors

    #region Methods

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <summary>
    /// Creates the retention policy of a configuration.
    /// </summary>
    /// <param name="config">The backend configuration</param>
    /// <returns>The policy; a null period is unlimited</returns>
    internal static RetentionPolicy CreatePolicy(BackendConfig config)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Runs one fold pass: bounded fold steps until nothing is left to fold.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that completes when the pass ends</returns>
    internal Task RollUpAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003

    // Skeleton: removed with the implementation
#pragma warning disable S2325, RH2003
    /// <summary>
    /// Runs one removal pass: the kill scan, then bounded removal steps until nothing is left to remove.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that completes when the pass ends</returns>
    internal Task RemoveAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore S2325, RH2003

    #endregion // Methods

    #region BackgroundService

    // Skeleton: removed with the implementation
#pragma warning disable RH2003
    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        throw new NotImplementedException();
    }
#pragma warning restore RH2003

    #endregion // BackgroundService
}