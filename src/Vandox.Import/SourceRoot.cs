using Vandox.Core.IO;

namespace Vandox.Import;

/// <summary>
/// The opened import root: a directory, or the directory that holds a root file.
/// </summary>
internal sealed class SourceRoot : IDisposable
{
    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceRoot"/> class.
    /// </summary>
    /// <param name="root">The directory below which files are opened</param>
    /// <param name="file">The name of the root file in the directory; empty when the root is a directory</param>
    private SourceRoot(SecureRoot root, string file)
    {
        Root = root;
        File = file;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets the directory below which files are opened.
    /// </summary>
    internal SecureRoot Root { get; }

    /// <summary>
    /// Gets the name of the root file in <see cref="Root"/>; empty when the root is a directory.
    /// </summary>
    internal string File { get; }

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Resolves a path once, checks it and opens it: a directory as the root, a regular file through the directory that holds
    /// it. Nothing is opened before the type was checked.
    /// </summary>
    /// <param name="path">The path given by the operator</param>
    /// <param name="withoutKernel">Whether to resolve paths without <c>openat2</c></param>
    /// <returns>The opened root</returns>
    /// <exception cref="IOException">The path is empty, missing, or neither a directory nor a regular file</exception>
    internal static SourceRoot Open(string path, bool withoutKernel)
    {
        if (path.Length == 0)
        {
            throw new SafeIoException("empty path");
        }

        var resolved = Resolve(Path.GetFullPath(path));

        switch (FileProbe.GetKind(resolved, followLinks: false))
        {
            case FileKind.Directory:
                return new SourceRoot(OpenRoot(resolved, withoutKernel), string.Empty);
            case FileKind.Regular:
                return new SourceRoot(OpenRoot(Path.GetDirectoryName(resolved) ?? "/", withoutKernel), Path.GetFileName(resolved));
            case FileKind.Missing:
                throw new SafeIoException("no such file or directory");
            default:
                throw new SafeIoException("neither a directory nor a regular file");
        }
    }

    /// <summary>
    /// Opens a directory as the root of the import.
    /// </summary>
    /// <param name="path">The full path</param>
    /// <param name="withoutKernel">Whether to resolve paths without <c>openat2</c></param>
    /// <returns>The root</returns>
    private static SecureRoot OpenRoot(string path, bool withoutKernel)
    {
        return withoutKernel ? SecureRoot.OpenWithoutKernelResolution(path) : SecureRoot.Open(path);
    }

    /// <summary>
    /// Follows symbolic links of the last path element until the target is not a link.
    /// </summary>
    /// <param name="path">The full path</param>
    /// <returns>The full path of the target</returns>
    private static string Resolve(string path)
    {
        var current = path;

        for (var depth = 0; depth < 40 && FileProbe.GetKind(current, followLinks: false) == FileKind.Symlink; depth++)
        {
            var target = new FileInfo(current).LinkTarget;

            if (target is null)
            {
                break;
            }

            current = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? "/");
        }

        return current;
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        Root.Dispose();
    }

    #endregion // IDisposable
}