using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Vandox.Backend.Hosting;
using Vandox.Core.Configuration;
using Vandox.Storage;

namespace Vandox.Backend.Retention;

/// <summary>
/// The background job that folds metric values into rollups and removes expired data in bounded steps.
/// </summary>
public sealed class RetentionService : BackgroundService
{
    #region Constants

    private const string FoldOperation = "fold";

    private const string ScanOperation = "scan";

    private const string RemoveOperation = "remove";

    private static readonly TimeSpan _tick = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan _removalInterval = TimeSpan.FromHours(1);

    #endregion // Constants

    #region Fields

    private readonly IRetentionStore _store;
    private readonly RetentionPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly KillHoldScanner _scanner;
    private DateTimeOffset? _lastRemoval;

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
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _policy = policy;
        _clock = clock;
        _logger = logger;
        _scanner = new KillHoldScanner(store);
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Creates the retention policy of a configuration.
    /// </summary>
    /// <param name="config">The backend configuration</param>
    /// <returns>The policy; a null period is unlimited</returns>
    /// <exception cref="InvalidOperationException">A period of the configuration is not valid; the loader refuses such a configuration</exception>
    internal static RetentionPolicy CreatePolicy(BackendConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new RetentionPolicy
               {
                   Raw = Parse(config.Retention.Raw),
                   FiveMinuteRollups = Parse(config.Retention.FiveMinuteRollups),
                   HourlyRollups = Parse(config.Retention.HourlyRollups),
                   Logs = Parse(config.Retention.Logs),
                   Snapshots = Parse(config.Retention.Snapshots),
                   Events = Parse(config.Retention.Events),
                   Incidents = Parse(config.Retention.Incidents),
                   KillWindowBefore = config.Incidents.GetWindowBefore(),
                   KillWindowAfter = config.Incidents.GetWindowAfter()
               };
    }

    /// <summary>
    /// Runs one fold pass: bounded fold steps until nothing is left to fold.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that completes when the pass ends</returns>
    internal async Task RollUpAsync(CancellationToken cancellationToken)
    {
        var values = 0L;
        var buckets = 0L;
        StoreException? failure = null;

        try
        {
            bool more;

            do
            {
                var step = await _store.FoldRollupsAsync(_policy, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

                values += step.Values;
                buckets += step.Buckets;
                more = step.More;
            }
            while (more && cancellationToken.IsCancellationRequested == false);
        }
        catch (StoreException exception)
        {
            failure = exception;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the steps done stay committed.
        }

        if (values > 0)
        {
            _logger.RetentionRolledUp(values, buckets);
        }

        if (failure is not null)
        {
            LogFailure(FoldOperation, failure);
        }
    }

    /// <summary>
    /// Runs one removal pass: the kill scan, then bounded removal steps until nothing is left to remove.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that completes when the pass ends</returns>
    internal async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var totals = new RetentionRemovalTotals();
        var operation = ScanOperation;
        var completed = false;
        StoreException? failure = null;

        try
        {
            await _scanner.ScanAsync(_policy, now, cancellationToken).ConfigureAwait(false);

            operation = RemoveOperation;
            completed = await RemoveStepsAsync(now, totals, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException exception)
        {
            failure = exception;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the steps done stay committed.
        }

        totals.Log(_logger);

        if (failure is not null)
        {
            LogFailure(operation, failure);
        }
        else if (completed)
        {
            _lastRemoval = now;
        }
    }

    /// <summary>
    /// Parses a period of the configuration.
    /// </summary>
    /// <param name="text">The period, such as <c>30d</c>, or <c>unlimited</c></param>
    /// <returns>The period, or <c>null</c> for unlimited</returns>
    private static TimeSpan? Parse(string text)
    {
        if (RetentionPeriod.TryParse(text, out var period))
        {
            return period;
        }

        throw new InvalidOperationException("retention: a period of the configuration is not valid");
    }

    /// <summary>
    /// Determines whether a store error is a busy database.
    /// </summary>
    /// <param name="exception">The error</param>
    /// <returns><c>true</c> when SQLite reported that another connection holds the lock</returns>
    private static bool IsBusy(StoreException exception)
    {
        return exception.InnerException is SqliteException { SqliteErrorCode: 5 };
    }

    /// <summary>
    /// Runs the removal steps, each continuing at the cursor of the one before.
    /// </summary>
    /// <param name="now">The time of the pass</param>
    /// <param name="totals">Adds up the counts of the steps</param>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that returns <c>true</c> when nothing is left to remove, <c>false</c> when the pass was stopped</returns>
    private async Task<bool> RemoveStepsAsync(DateTimeOffset now, RetentionRemovalTotals totals, CancellationToken cancellationToken)
    {
        RemovalCursor? cursor = null;

        while (cancellationToken.IsCancellationRequested == false)
        {
            var step = await _store.RemoveExpiredAsync(_policy, now, cursor, cancellationToken).ConfigureAwait(false);

            totals.Add(step);

            if (step.More == false)
            {
                return true;
            }

            cursor = step.Next;
        }

        return false;
    }

    /// <summary>
    /// Logs a failed step: a busy database as a warning, any other error with the store's message.
    /// </summary>
    /// <param name="operation">The operation: fold, scan or remove</param>
    /// <param name="exception">The error</param>
    private void LogFailure(string operation, StoreException exception)
    {
        if (IsBusy(exception))
        {
            _logger.RetentionStepBusy(operation);
        }
        else
        {
            _logger.RetentionStepFailed(operation, exception.Message);
        }
    }

    /// <summary>
    /// Runs the fold pass and, when an hour has passed since the last removal pass, the removal pass.
    /// </summary>
    /// <param name="cancellationToken">Stops the pass between steps</param>
    /// <returns>A task that completes when the passes end</returns>
    private async Task RunPassesAsync(CancellationToken cancellationToken)
    {
        await RollUpAsync(cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (_lastRemoval is null || _clock.GetUtcNow() - _lastRemoval >= _removalInterval)
        {
            await RemoveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    #endregion // Methods

    #region BackgroundService

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        using var timer = new PeriodicTimer(_tick, _clock);

        try
        {
            do
            {
                await RunPassesAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    #endregion // BackgroundService
}