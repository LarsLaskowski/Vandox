namespace Vandox.Storage.Tests;

/// <summary>
/// Finds files of the repository, such as <c>testdata/</c>, from the test output directory.
/// </summary>
internal static class RepositoryFiles
{
    #region Methods

    /// <summary>
    /// Gets the full path of a file of the repository.
    /// </summary>
    /// <param name="relative">The path relative to the repository root</param>
    /// <returns>The full path</returns>
    internal static string Path(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Vandox.slnx")))
            {
                return System.IO.Path.Combine(directory.FullName, relative);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }

    #endregion // Methods
}