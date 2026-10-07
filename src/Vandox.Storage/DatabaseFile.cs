using Vandox.Core.IO;

namespace Vandox.Storage;

/// <summary>
/// Prepares the database file: creates it exclusively with mode 0600, and refuses anything that is not a regular file.
/// SQLite follows a symbolic link of the main file and would place the database and its <c>-wal</c> and <c>-shm</c>
/// files at the link target, so links are never followed.
/// </summary>
internal static class DatabaseFile
{
    #region Methods

    /// <summary>
    /// Creates the database file exclusively, or checks that an existing entry is a regular file, and checks the
    /// <c>-wal</c> and <c>-shm</c> files if present.
    /// </summary>
    /// <param name="path">The path of the database file</param>
    /// <returns><c>true</c> when the file was created</returns>
    /// <exception cref="StoreException">The entry cannot be created or is not a regular file</exception>
    internal static bool Prepare(string path)
    {
        var created = CreateOrCheck(path);

        RequireRegularIfPresent($"{path}-wal");
        RequireRegularIfPresent($"{path}-shm");

        return created;
    }

    /// <summary>
    /// Creates the file exclusively with mode 0600, or checks the existing entry.
    /// </summary>
    /// <param name="path">The path</param>
    /// <returns><c>true</c> when the file was created</returns>
    private static bool CreateOrCheck(string path)
    {
        try
        {
            var options = new FileStreamOptions
                          {
                              Mode = FileMode.CreateNew,
                              Access = FileAccess.ReadWrite,
                              Share = FileShare.ReadWrite
                          };

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(path, options);

            return true;
        }
        catch (IOException) when (FileProbe.GetKind(path, followLinks: false) != FileKind.Missing)
        {
            RequireRegular(path);

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new StoreException($"store: creating {path}: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Refuses a path that exists and is not a regular file.
    /// </summary>
    /// <param name="path">The path</param>
    private static void RequireRegularIfPresent(string path)
    {
        if (FileProbe.GetKind(path, followLinks: false) != FileKind.Missing)
        {
            RequireRegular(path);
        }
    }

    /// <summary>
    /// Refuses a path that is not a regular file: a symbolic link, a directory or a device.
    /// </summary>
    /// <param name="path">The path</param>
    private static void RequireRegular(string path)
    {
        var kind = FileProbe.GetKind(path, followLinks: false);

        if (kind != FileKind.Regular)
        {
            throw new StoreException($"store: checking {path}: not a regular file ({kind.ToString().ToLowerInvariant()})");
        }
    }

    #endregion // Methods
}