namespace Vandox.Core.Tests;

/// <summary>
/// Locates files of the repository for tests that check them.
/// </summary>
internal static class RepositoryFiles
{
    #region Methods

    /// <summary>
    /// Returns the absolute path of a file of the repository.
    /// </summary>
    /// <param name="relative">The path relative to the repository root</param>
    /// <returns>The absolute path</returns>
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