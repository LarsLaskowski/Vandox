namespace Vandox.Core.Tests;

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
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vandox-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the path of the directory.
    /// </summary>
    internal string Path { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Writes a file into the directory.
    /// </summary>
    /// <param name="name">The name of the file</param>
    /// <param name="content">The content</param>
    /// <returns>The path of the file</returns>
    internal string Write(string name, string content)
    {
        var path = System.IO.Path.Combine(Path, name);

        File.WriteAllText(path, content);

        return path;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }

    #endregion // IDisposable
}