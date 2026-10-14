using Microsoft.Extensions.Logging;

namespace Vandox.Backend.Tests;

/// <summary>
/// A logger provider that hands one <see cref="ListLogger"/> to every category, so a component's <c>ILogger&lt;T&gt;</c> writes into it.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    #region Fields

    private readonly ListLogger _logger;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ListLoggerProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger every category gets</param>
    internal ListLoggerProvider(ListLogger logger)
    {
        _logger = logger;
    }

    #endregion // Constructors

    #region ILoggerProvider

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return _logger;
    }

    #endregion // ILoggerProvider

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
    }

    #endregion // IDisposable
}