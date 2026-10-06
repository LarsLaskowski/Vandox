using Vandox.Core.LogParsing;

namespace Vandox.Core.Tests;

/// <summary>
/// A parser double that rates files by name and claims or ignores them.
/// </summary>
internal sealed class FakeParser : ILogParser
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeParser"/> class.
    /// </summary>
    /// <param name="type">The type</param>
    /// <param name="confidence">The confidence it reports for a file whose name contains its type</param>
    internal FakeParser(string type, Confidence confidence)
    {
        Type = type;
        Confidence = confidence;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the confidence it reports for a matching file.
    /// </summary>
    internal Confidence Confidence { get; }

    #endregion // Properties

    #region ILogParser

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public Confidence Detect(LogFile file, ReadOnlySpan<byte> head)
    {
        return file.Name.Contains(Type, StringComparison.Ordinal) ? Confidence : Confidence.NoMatch;
    }

    /// <inheritdoc />
    /// <returns>A task that completes when the work is done</returns>
    public Task ParseAsync(LogFile file, Stream input, IRecordEmitter output, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    #endregion // ILogParser
}