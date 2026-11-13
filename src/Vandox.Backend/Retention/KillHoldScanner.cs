using Vandox.Storage;

namespace Vandox.Backend.Retention;

// Skeleton: removed with the implementation
#pragma warning disable S4487, S2325, RH2003
/// <summary>
/// Scans the stored kernel events for OOM kills and keeps the window of each kill as a hold.
/// </summary>
internal sealed class KillHoldScanner
{
    #region Fields

    private readonly IRetentionStore _store;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="KillHoldScanner"/> class.
    /// </summary>
    /// <param name="store">The retention store</param>
    internal KillHoldScanner(IRetentionStore store)
    {
        _store = store;
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Scans the kernel events not scanned yet and saves the holds of their OOM kills.
    /// </summary>
    /// <param name="policy">The retention policy that holds the kill window</param>
    /// <param name="now">The current time</param>
    /// <param name="cancellationToken">Cancels the scan</param>
    /// <returns>A task that returns the number of holds added</returns>
    internal Task<int> ScanAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    #endregion // Methods
}
#pragma warning restore S4487, S2325, RH2003