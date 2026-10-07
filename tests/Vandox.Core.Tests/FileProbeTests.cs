using System.Diagnostics;

using Vandox.Core.IO;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="FileProbe"/>
/// </summary>
[TestClass]
public class FileProbeTests
{
    #region Methods

    /// <summary>
    /// The kind of regular files, directories, links and missing entries is reported.
    /// </summary>
    [TestMethod]
    public void FileProbeGetKindReportsKinds()
    {
        // Arrange
        using var directory = new TempDirectory();
        var file = directory.Write("file.txt", "x");
        var link = Path.Combine(directory.Path, "link");

        File.CreateSymbolicLink(link, file);

        // Act
        var regular = FileProbe.GetKind(file, true);
        var folder = FileProbe.GetKind(directory.Path, true);
        var missing = FileProbe.GetKind(Path.Combine(directory.Path, "nothing"), true);
        var belowFile = FileProbe.GetKind(Path.Combine(file, "below"), true);
        var followed = FileProbe.GetKind(link, true);
        var notFollowed = FileProbe.GetKind(link, false);

        // Assert
        Assert.AreEqual(FileKind.Regular, regular, "regular file");
        Assert.AreEqual(FileKind.Directory, folder, "directory");
        Assert.AreEqual(FileKind.Missing, missing, "missing entry");
        Assert.AreEqual(FileKind.Missing, belowFile, "entry below a file");
        Assert.AreEqual(FileKind.Regular, followed, "link followed");
        Assert.AreEqual(FileKind.Symlink, notFollowed, "link not followed");
    }

    /// <summary>
    /// Special files are not regular files.
    /// </summary>
    [TestMethod]
    public void FileProbeGetKindReportsDeviceAndFifoAsOther()
    {
        // Arrange
        using var directory = new TempDirectory();
        var fifo = Path.Combine(directory.Path, "fifo");

        // Act
        var device = FileProbe.GetKind("/dev/null", true);
        var made = MakeFifo(fifo);
        var kind = made ? FileProbe.GetKind(fifo, true) : FileKind.Other;

        // Assert
        Assert.AreEqual(FileKind.Other, device, "device");
        Assert.AreEqual(FileKind.Other, kind, "FIFO");
    }

    /// <summary>
    /// A regular file is opened and read; a link in the last element is followed only when asked.
    /// </summary>
    [TestMethod]
    public void FileProbeOpenRegularOpensFileAndRefusesLinkWhenNotFollowing()
    {
        // Arrange
        using var directory = new TempDirectory();
        var file = directory.Write("file.txt", "content");
        var link = Path.Combine(directory.Path, "link");

        File.CreateSymbolicLink(link, file);

        // Act
        using var stream = FileProbe.OpenRegular(file, false);
        using var followed = FileProbe.OpenRegular(link, true);
        var text = new StreamReader(stream).ReadToEnd();

        // Assert
        Assert.AreEqual("content", text, "content of the file");
        Assert.IsTrue(followed.CanRead, "a link is followed when asked");
        Assert.ThrowsExactly<SafeIoException>(() => FileProbe.OpenRegular(link, false), "a link is refused when not following");
    }

    /// <summary>
    /// A FIFO is refused without blocking, and so are a device and a directory.
    /// </summary>
    [TestMethod]
    public void FileProbeOpenRegularRefusesSpecialFiles()
    {
        // Arrange
        using var directory = new TempDirectory();
        var fifo = Path.Combine(directory.Path, "fifo");

        // Act and Assert
        Assert.ThrowsExactly<SafeIoException>(() => FileProbe.OpenRegular("/dev/null", true), "device");
        Assert.ThrowsExactly<SafeIoException>(() => FileProbe.OpenRegular(Path.Combine(directory.Path, "missing"), true), "missing file");

        if (MakeFifo(fifo))
        {
            Assert.ThrowsExactly<SafeIoException>(() => FileProbe.OpenRegular(fifo, true), "FIFO");
        }
    }

    /// <summary>
    /// The type and the size of an open file are reported.
    /// </summary>
    [TestMethod]
    public void FileProbeGetKindOfHandleReportsSize()
    {
        // Arrange
        using var directory = new TempDirectory();
        var file = directory.Write("file.txt", "12345");
        using var stream = FileProbe.OpenRegular(file, true);

        // Act
        var kind = FileProbe.GetKind(stream.SafeFileHandle, out var size);

        // Assert
        Assert.AreEqual(FileKind.Regular, kind, "kind");
        Assert.AreEqual(5L, size, "size");
        Assert.IsTrue(FileProbe.IsPrecise, "the precise probe is available on Linux");
    }

    /// <summary>
    /// Creates a FIFO with the system tool.
    /// </summary>
    /// <param name="path">The path of the FIFO</param>
    /// <returns><c>true</c> when the FIFO was created</returns>
    private static bool MakeFifo(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("mkfifo", path));

            process?.WaitForExit();

            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    #endregion // Methods
}