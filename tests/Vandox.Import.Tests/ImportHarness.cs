using Microsoft.Extensions.Time.Testing;

using Vandox.Core.LogParsing;

namespace Vandox.Import.Tests;

/// <summary>
/// Sets up an import run over a temporary directory with the test parser and a fake store.
/// </summary>
internal sealed class ImportHarness : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportHarness"/> class.
    /// </summary>
    internal ImportHarness()
    {
        Directory = new TempDirectory();
        Parser = new LineParser("test");
        Store = new FakeImportStore();
        Clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the temporary directory that is imported.
    /// </summary>
    internal TempDirectory Directory { get; }

    /// <summary>
    /// Gets the parser.
    /// </summary>
    internal LineParser Parser { get; }

    /// <summary>
    /// Gets the store.
    /// </summary>
    internal FakeImportStore Store { get; }

    /// <summary>
    /// Gets the clock.
    /// </summary>
    internal FakeTimeProvider Clock { get; }

    /// <summary>
    /// Gets the progress events of the runs.
    /// </summary>
    internal List<ImportProgress> Events { get; } = [];

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Writes a file below the directory, creating its folders.
    /// </summary>
    /// <param name="relative">The path below the directory</param>
    /// <param name="content">The bytes</param>
    /// <returns>The full path</returns>
    internal string Write(string relative, byte[] content)
    {
        var path = Path.Combine(Directory.Path, relative);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);

        return path;
    }

    /// <summary>
    /// Writes a text file below the directory.
    /// </summary>
    /// <param name="relative">The path below the directory</param>
    /// <param name="content">The text</param>
    /// <returns>The full path</returns>
    internal string Write(string relative, string content)
    {
        return Write(relative, TestInputs.Bytes(content));
    }

    /// <summary>
    /// Runs an import of the directory (or another root).
    /// </summary>
    /// <param name="cancellationToken">Interrupts the run</param>
    /// <param name="root">The root; the temporary directory when <c>null</c></param>
    /// <param name="tweak">Changes the options before the run</param>
    /// <returns>A task that returns the run</returns>
    internal Task<ImportRun> RunAsync(CancellationToken cancellationToken, string? root = null, Action<ImportOptions>? tweak = null)
    {
        var options = new ImportOptions
                      {
                          Parsers = new ParserRegistry([Parser]),
                          Store = Store,
                          Clock = Clock,
                          Progress = Events.Add
                      };

        tweak?.Invoke(options);

        return Importer.RunAsync(root ?? Directory.Path, options, cancellationToken);
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Directory.Dispose();
    }

    #endregion // IDisposable
}