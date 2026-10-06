using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Vandox.Core.IO;

/// <summary>
/// Reports the type of a file system entry without opening it, and opens files without blocking and without
/// following links. .NET reports a FIFO or a device as a normal file, so on Linux the type comes from <c>statx</c>.
/// </summary>
public static partial class FileProbe
{
    #region Constants

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x1;
    private const uint StatxSize = 0x200;
    private const int ModeTypeMask = 0xF000;
    private const int ModeRegular = 0x8000;
    private const int ModeDirectory = 0x4000;
    private const int ModeSymlink = 0xA000;
    private const int StatxBufferBytes = 256;
    private const int ModeOffset = 28;
    private const int SizeOffset = 40;
    private const int ReadOnlyFlag = 0;
    private const int NonBlockFlag = 0x800;
    private const int CloseOnExecFlag = 0x80000;
    private const int NoFollowFlagX86 = 0x20000;
    private const int NoFollowFlagArm = 0x8000;
    private const int ErrnoNoEntry = 2;
    private const int ErrnoNotDirectory = 20;

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the precise probe is available on this platform.
    /// </summary>
    public static bool IsPrecise => OperatingSystem.IsLinux();

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Returns the type of the entry at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="followLinks">Whether a symbolic link is resolved; otherwise the link itself is reported</param>
    /// <returns>The type of the entry</returns>
    public static FileKind GetKind(string path, bool followLinks)
    {
        if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[StatxBufferBytes];
            var flags = followLinks ? 0 : AtSymlinkNoFollow;
            var result = Statx(AtFdCwd, path, flags, StatxType, buffer);

            return result == 0 ? KindOfMode(buffer) : KindOfError();
        }

        return GetKindPortable(path, followLinks);
    }

    /// <summary>
    /// Returns the type and the size of the file behind an open handle.
    /// </summary>
    /// <param name="handle">The open handle</param>
    /// <param name="size">The size in bytes; -1 when unknown</param>
    /// <returns>The type of the file</returns>
    public static FileKind GetKind(SafeFileHandle handle, out long size)
    {
        size = -1;

        if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[StatxBufferBytes];
            var result = StatxHandle(handle, string.Empty, AtEmptyPath, StatxType | StatxSize, buffer);

            if (result == 0)
            {
                size = (long)BitConverter.ToUInt64(buffer, SizeOffset);

                return KindOfMode(buffer);
            }

            return FileKind.Other;
        }

        size = RandomAccess.GetLength(handle);

        return FileKind.Regular;
    }

    /// <summary>
    /// Opens a regular file for reading without following a link in the last path element and without blocking on
    /// a FIFO, and checks that what was opened is a regular file.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="followLinks">Whether a symbolic link as the last element is followed</param>
    /// <returns>A read-only stream</returns>
    /// <exception cref="IOException">The path is not a regular file or cannot be opened</exception>
    public static FileStream OpenRegular(string path, bool followLinks)
    {
        if (OperatingSystem.IsLinux())
        {
            var flags = ReadOnlyFlag | NonBlockFlag | CloseOnExecFlag | (followLinks ? 0 : NoFollowFlag());
            var descriptor = Open(path, flags);

            if (descriptor < 0)
            {
                throw new SafeIoException($"cannot open the file (errno {Marshal.GetLastPInvokeError()})");
            }

            var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

            if (GetKind(handle, out _) != FileKind.Regular)
            {
                handle.Dispose();

                throw new SafeIoException("not a regular file");
            }

            return new FileStream(handle, FileAccess.Read, 1, false);
        }

        if (GetKindPortable(path, followLinks) != FileKind.Regular)
        {
            throw new SafeIoException("not a regular file");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
    }

    /// <summary>
    /// Classifies the mode of a <c>statx</c> result.
    /// </summary>
    /// <param name="buffer">The <c>statx</c> buffer</param>
    /// <returns>The type</returns>
    private static FileKind KindOfMode(byte[] buffer)
    {
        var type = BitConverter.ToUInt16(buffer, ModeOffset) & ModeTypeMask;

        return type switch
               {
                   ModeRegular => FileKind.Regular,
                   ModeDirectory => FileKind.Directory,
                   ModeSymlink => FileKind.Symlink,
                   _ => FileKind.Other
               };
    }

    /// <summary>
    /// Classifies a failed <c>statx</c>: a missing entry (or a missing parent directory) is <see cref="FileKind.Missing"/>.
    /// </summary>
    /// <returns>The type</returns>
    private static FileKind KindOfError()
    {
        var error = Marshal.GetLastPInvokeError();

        return error == ErrnoNoEntry || error == ErrnoNotDirectory ? FileKind.Missing : FileKind.Other;
    }

    /// <summary>
    /// Returns the <c>O_NOFOLLOW</c> flag of the CPU architecture.
    /// </summary>
    /// <returns>The flag</returns>
    private static int NoFollowFlag()
    {
        return RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? NoFollowFlagArm : NoFollowFlagX86;
    }

    /// <summary>
    /// Approximates the type with the attributes .NET reports, for platforms without <c>statx</c>.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="followLinks">Whether a symbolic link is resolved</param>
    /// <returns>The type</returns>
    private static FileKind GetKindPortable(string path, bool followLinks)
    {
        var info = new FileInfo(path);

        if (info.Attributes == (FileAttributes)(-1))
        {
            return FileKind.Missing;
        }

        if (info.LinkTarget is not null)
        {
            return followLinks ? KindOfTarget(info) : FileKind.Symlink;
        }

        return info.Attributes.HasFlag(FileAttributes.Directory) ? FileKind.Directory : FileKind.Regular;
    }

    /// <summary>
    /// Returns the type of what a link points to.
    /// </summary>
    /// <param name="info">The link</param>
    /// <returns>The type</returns>
    private static FileKind KindOfTarget(FileInfo info)
    {
        var target = info.ResolveLinkTarget(true);

        if (target is DirectoryInfo { Exists: true })
        {
            return FileKind.Directory;
        }

        return target is FileInfo { Exists: true } ? FileKind.Regular : FileKind.Missing;
    }

    /// <summary>
    /// Calls <c>statx</c> for a path.
    /// </summary>
    /// <param name="directory">The directory descriptor</param>
    /// <param name="path">The path</param>
    /// <param name="flags">The flags</param>
    /// <param name="mask">The wanted fields</param>
    /// <param name="buffer">The result buffer of 256 bytes</param>
    /// <returns>0 on success</returns>
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, byte[] buffer);

    /// <summary>
    /// Calls <c>statx</c> for an open descriptor.
    /// </summary>
    /// <param name="descriptor">The descriptor</param>
    /// <param name="path">An empty text</param>
    /// <param name="flags">The flags, including AT_EMPTY_PATH</param>
    /// <param name="mask">The wanted fields</param>
    /// <param name="buffer">The result buffer of 256 bytes</param>
    /// <returns>0 on success</returns>
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int StatxHandle(SafeFileHandle descriptor, string path, int flags, uint mask, byte[] buffer);

    /// <summary>
    /// Calls <c>open</c>.
    /// </summary>
    /// <param name="path">The path</param>
    /// <param name="flags">The flags</param>
    /// <returns>The descriptor, or -1</returns>
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    #endregion // Methods
}