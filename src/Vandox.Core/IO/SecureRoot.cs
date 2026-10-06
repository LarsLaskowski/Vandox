using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Vandox.Core.IO;

/// <summary>
/// A directory below which files are opened without ever following a symbolic link or leaving the directory. On a Linux kernel
/// of version 5.6 or newer every open is resolved by the kernel (<c>openat2</c> with <c>RESOLVE_BENEATH</c> and
/// <c>RESOLVE_NO_SYMLINKS</c>), which no concurrent change of the tree can fool. On older kernels (for example the 4.4 kernel
/// of many NAS systems) the file is opened without following a link in its last path element after the type of every entry
/// was checked, which leaves a small window for a local user who can change the tree during the import.
/// </summary>
public sealed partial class SecureRoot : IDisposable
{
    #region Constants

    private const long SyscallOpenat2 = 437;
    private const ulong ResolveNoMagicLinks = 0x02;
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const int ReadOnlyFlag = 0;
    private const int NonBlockFlag = 0x800;
    private const int CloseOnExecFlag = 0x80000;
    private const int PathFlag = 0x200000;
    private const int ErrnoNoSys = 38;
    private const int ErrnoPermission = 1;
    private const int ErrnoInvalid = 22;

    #endregion // Constants

    #region Fields

    private readonly string _path;
    private readonly SafeFileHandle? _root;
    private bool _kernelResolves;

    #endregion // Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="SecureRoot"/> class.
    /// </summary>
    /// <param name="path">The path of the directory</param>
    /// <param name="root">The open directory, when the platform provides one</param>
    private SecureRoot(string path, SafeFileHandle? root)
    {
        _path = path;
        _root = root;
        _kernelResolves = root is not null;
    }

    #endregion // Constructors

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the kernel resolves every path beneath the root without symbolic links. It is known
    /// after the first open and turns <c>false</c> when the kernel does not support it.
    /// </summary>
    public bool KernelResolves => _kernelResolves;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Opens the directory at <paramref name="path"/>, which the caller resolved before and checked to be a directory.
    /// </summary>
    /// <param name="path">The path of the directory</param>
    /// <returns>The root</returns>
    /// <exception cref="IOException">The directory cannot be opened</exception>
    public static SecureRoot Open(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            var directoryFlag = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x4000 : 0x10000;
            var descriptor = Open(path, ReadOnlyFlag | directoryFlag | CloseOnExecFlag);

            if (descriptor < 0)
            {
                throw new SafeIoException($"cannot open the directory (errno {Marshal.GetLastPInvokeError()})");
            }

            return new SecureRoot(path, new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
        }

        return new SecureRoot(path, null);
    }

    /// <summary>
    /// Returns the type of the entry <paramref name="relative"/> below the root, never following a link.
    /// </summary>
    /// <param name="relative">The slash-separated path below the root; <c>.</c> is the root itself</param>
    /// <returns>The type of the entry</returns>
    public FileKind GetKind(string relative)
    {
        return FileProbe.GetKind(FullPath(relative), followLinks: false);
    }

    /// <summary>
    /// Lists the names of the entries of the directory <paramref name="relative"/>. The names are produced lazily, so a
    /// caller can stop counting at a limit.
    /// </summary>
    /// <param name="relative">The slash-separated path below the root; <c>.</c> is the root itself</param>
    /// <returns>The names, in no particular order</returns>
    /// <exception cref="IOException">The directory is a link, leaves the root or cannot be read</exception>
    public IEnumerable<string> ListNames(string relative)
    {
        if (relative != ".")
        {
            using var check = OpenBeneath(relative, PathFlag);

            if (check is null && _kernelResolves)
            {
                throw new SafeIoException("the directory cannot be opened beneath the root");
            }
        }

        return Directory.EnumerateFileSystemEntries(FullPath(relative)).Select(Path.GetFileName).OfType<string>();
    }

    /// <summary>
    /// Opens the regular file <paramref name="relative"/> below the root for reading, without following a link and without
    /// blocking on a FIFO.
    /// </summary>
    /// <param name="relative">The slash-separated path below the root</param>
    /// <returns>A read-only stream</returns>
    /// <exception cref="IOException">The path is not a regular file or cannot be opened</exception>
    public FileStream OpenRegular(string relative)
    {
        var handle = OpenBeneath(relative, ReadOnlyFlag | NonBlockFlag);

        if (handle is null)
        {
            return FileProbe.OpenRegular(FullPath(relative), followLinks: false);
        }

        if (FileProbe.GetKind(handle, out _) != FileKind.Regular)
        {
            handle.Dispose();

            throw new SafeIoException("not a regular file");
        }

        return new FileStream(handle, FileAccess.Read, 1, false);
    }

    /// <summary>
    /// Calls <c>open</c>.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="flags">The flags</param>
    /// <returns>The descriptor, or -1</returns>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    /// <summary>
    /// Calls <c>openat2</c> through <c>syscall</c>.
    /// </summary>
    /// <param name="number">The system call number</param>
    /// <param name="directory">The directory the path is relative to</param>
    /// <param name="path">The path</param>
    /// <param name="how">The open parameters</param>
    /// <param name="size">The size of the parameters</param>
    /// <returns>The descriptor, or -1</returns>
    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial long Openat2(long number, SafeFileHandle directory, string path, ref OpenHow how, nuint size);

    /// <summary>
    /// Returns the full path of an entry below the root.
    /// </summary>
    /// <param name="relative">The path below the root</param>
    /// <returns>The full path</returns>
    private string FullPath(string relative)
    {
        return relative == "." ? _path : Path.Combine(_path, relative);
    }

    /// <summary>
    /// Opens an entry with <c>openat2</c>; <c>null</c> when the kernel does not support it, and an exception when the kernel
    /// refuses the path because it holds a link or leaves the root.
    /// </summary>
    /// <param name="relative">The path below the root</param>
    /// <param name="flags">The open flags</param>
    /// <returns>The handle, or <c>null</c> when <c>openat2</c> is not available</returns>
    private SafeFileHandle? OpenBeneath(string relative, int flags)
    {
        if (_root is not null && _kernelResolves)
        {
            return CallOpenat2(_root, relative, flags);
        }

        return null;
    }

    /// <summary>
    /// Calls <c>openat2</c> and remembers when the kernel does not support it.
    /// </summary>
    /// <param name="root">The open root directory</param>
    /// <param name="relative">The path below the root</param>
    /// <param name="flags">The open flags</param>
    /// <returns>The handle, or <c>null</c> when <c>openat2</c> is not available</returns>
    private SafeFileHandle? CallOpenat2(SafeFileHandle root, string relative, int flags)
    {
        var how = new OpenHow
                  {
                      Flags = (ulong)(flags | CloseOnExecFlag),
                      Mode = 0,
                      Resolve = ResolveBeneath | ResolveNoSymlinks | ResolveNoMagicLinks
                  };
        var descriptor = (int)Openat2(SyscallOpenat2, root, relative, ref how, (nuint)Marshal.SizeOf<OpenHow>());

        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var errno = Marshal.GetLastPInvokeError();

        if (errno is ErrnoNoSys or ErrnoPermission or ErrnoInvalid)
        {
            _kernelResolves = false;

            return null;
        }

        throw new SafeIoException($"cannot open the entry beneath the root (errno {errno})");
    }

    #endregion // Methods

    #region IDisposable

    /// <inheritdoc />
    public void Dispose()
    {
        _root?.Dispose();
    }

    #endregion // IDisposable
}