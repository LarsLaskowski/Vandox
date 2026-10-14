using Vandox.Backend.Security;

namespace Vandox.Backend.Tests;

/// <summary>
/// An <see cref="IPasswordVerifier"/> that accepts one password, counts its calls, records overlapping calls and can block until it is released.
/// </summary>
internal sealed class ScriptedPasswordVerifier : IPasswordVerifier, IDisposable
{
    #region Fields

    private readonly string _accepted;
    private readonly SemaphoreSlim _entered = new(0);
    private readonly ManualResetEventSlim _release = new(true);
    private readonly List<string> _passwords = [];
    private readonly Lock _lock = new();
    private int _running;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptedPasswordVerifier"/> class.
    /// </summary>
    /// <param name="accepted">The one password that verifies</param>
    internal ScriptedPasswordVerifier(string accepted)
    {
        _accepted = accepted;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the number of calls of <see cref="Verify"/> so far.
    /// </summary>
    internal int Calls
    {
        get
        {
            lock (_lock)
            {
                return _passwords.Count;
            }
        }
    }

    /// <summary>
    /// Gets the most calls of <see cref="Verify"/> that were running at the same time.
    /// </summary>
    internal int MaxConcurrent { get; private set; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Makes every following call of <see cref="Verify"/> block until <see cref="Release"/> is called.
    /// </summary>
    internal void BlockUntilReleased()
    {
        _release.Reset();
    }

    /// <summary>
    /// Lets the blocked calls and every following call continue.
    /// </summary>
    internal void Release()
    {
        _release.Set();
    }

    /// <summary>
    /// Waits until another call of <see cref="Verify"/> has started.
    /// </summary>
    /// <param name="cancellationToken">Stops the wait</param>
    /// <returns>A task that completes when a call has started that was not waited for before</returns>
    internal Task WaitUntilEnteredAsync(CancellationToken cancellationToken)
    {
        return _entered.WaitAsync(cancellationToken);
    }

    #endregion // Methods

    #region IPasswordVerifier

    /// <inheritdoc />
    public bool Verify(string password)
    {
        lock (_lock)
        {
            _passwords.Add(password);
            _running++;
            MaxConcurrent = Math.Max(MaxConcurrent, _running);
        }

        _entered.Release();
        _release.Wait(TimeSpan.FromSeconds(30));

        lock (_lock)
        {
            _running--;
        }

        return password == _accepted;
    }

    #endregion // IPasswordVerifier

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _entered.Dispose();
        _release.Dispose();
    }

    #endregion // IDisposable
}