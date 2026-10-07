namespace Vandox.Storage.Tests;

/// <summary>
/// A temporary directory that is deleted when it is disposed.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TempDirectory"/> class.
    /// </summary>
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vandox-store-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the path of the directory.
    /// </summary>
    internal string Path { get; }

    #endregion // Properties

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }

    #endregion // IDisposable
}