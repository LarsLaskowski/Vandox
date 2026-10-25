namespace Vandox.Backend.Tests;

/// <summary>
/// Samples the live managed heap from a second thread while a piece of work runs and keeps the maximum.
/// </summary>
internal sealed class HeapSampler : IDisposable
{
    #region Fields

    private static readonly TimeSpan _sampleInterval = TimeSpan.FromMilliseconds(5);

    private readonly Thread _thread;

    private readonly ManualResetEventSlim _stopped = new(false);

    private long _peak;

    private int _running = 1;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HeapSampler"/> class and starts sampling.
    /// </summary>
    internal HeapSampler()
    {
        _peak = RetainedBytes();
        _thread = new Thread(Sample)
                  {
                      IsBackground = true,
                      Name = "heap sampler"
                  };
        _thread.Start();
    }

    #endregion // Constructors

    #region Methods

    /// <summary>
    /// Returns the bytes the process retains after a full collection, which is how the heap-bound claims are measured.
    /// </summary>
    /// <returns>The number of bytes</returns>
    internal static long RetainedBytes()
    {
#pragma warning disable S1215
        return GC.GetTotalMemory(true);
#pragma warning restore S1215
    }

    /// <summary>
    /// Stops sampling and returns the highest heap seen.
    /// </summary>
    /// <returns>The number of bytes</returns>
    internal long Stop()
    {
        Volatile.Write(ref _running, 0);
        _stopped.Set();
        _thread.Join();

        return Volatile.Read(ref _peak);
    }

    /// <summary>
    /// Samples until the sampler is stopped.
    /// </summary>
    private void Sample()
    {
        while (Volatile.Read(ref _running) == 1)
        {
            var current = RetainedBytes();

            if (current > Volatile.Read(ref _peak))
            {
                Volatile.Write(ref _peak, current);
            }

            _stopped.Wait(_sampleInterval);
        }
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _stopped.Dispose();
    }

    #endregion // IDisposable
}