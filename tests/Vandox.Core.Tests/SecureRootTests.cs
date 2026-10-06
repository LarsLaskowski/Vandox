using Vandox.Core.IO;

namespace Vandox.Core.Tests;

/// <summary>
/// Tests for <see cref="SecureRoot"/>
/// </summary>
[TestClass]
public class SecureRootTests
{
    #region Methods

    /// <summary>
    /// Regular files below the root are opened, listed and classified.
    /// </summary>
    /// <param name="fallback">Whether paths are resolved without <c>openat2</c></param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SecureRootOpensListsAndClassifiesEntries(bool fallback)
    {
        // Arrange
        using var directory = new TempDirectory();

        Directory.CreateDirectory(Path.Combine(directory.Path, "sub"));
        directory.Write("a.log", "alpha");
        directory.Write("sub/b.log", "beta");

        using var root = fallback ? SecureRoot.OpenWithoutKernelResolution(directory.Path) : SecureRoot.Open(directory.Path);

        // Act
        using var first = root.OpenRegular("a.log");
        using var nested = root.OpenRegular("sub/b.log");
        var top = root.ListNames(".").Order().ToList();
        var below = root.ListNames("sub").ToList();

        // Assert
        Assert.AreEqual("alpha", new StreamReader(first).ReadToEnd(), "content of a.log");
        Assert.AreEqual("beta", new StreamReader(nested).ReadToEnd(), "content of sub/b.log");
        Assert.AreEqual("a.log,sub", string.Join(',', top), "top-level names");
        Assert.AreEqual("b.log", string.Join(',', below), "names below sub");
        Assert.AreEqual(FileKind.Regular, root.GetKind("a.log"), "regular file");
        Assert.AreEqual(FileKind.Directory, root.GetKind("sub"), "directory");
        Assert.AreEqual(FileKind.Missing, root.GetKind("nothing"), "missing entry");
    }

    /// <summary>
    /// A symbolic link is never followed, as the last element or as a directory in the path.
    /// </summary>
    /// <param name="fallback">Whether paths are resolved without <c>openat2</c></param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SecureRootRefusesLinks(bool fallback)
    {
        // Arrange
        using var outside = new TempDirectory();
        using var directory = new TempDirectory();

        outside.Write("secret.txt", "secret");
        Directory.CreateDirectory(Path.Combine(directory.Path, "real"));
        directory.Write("real/file.log", "x");
        File.CreateSymbolicLink(Path.Combine(directory.Path, "file-link"), Path.Combine(outside.Path, "secret.txt"));
        File.CreateSymbolicLink(Path.Combine(directory.Path, "dir-link"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(directory.Path, "inside-link"), Path.Combine(directory.Path, "real"));

        using var root = fallback ? SecureRoot.OpenWithoutKernelResolution(directory.Path) : SecureRoot.Open(directory.Path);

        // Act and Assert
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("file-link"), "link to a file outside");
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("dir-link/secret.txt"), "file below a linked directory");
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("inside-link/file.log"), "file below a link inside the root");
        Assert.ThrowsExactly<SafeIoException>(() => root.ListNames("dir-link").ToList(), "listing a linked directory");
        Assert.AreEqual(FileKind.Symlink, root.GetKind("file-link"), "a link is reported as a link");
    }

    /// <summary>
    /// Without <c>openat2</c> a parent reference in the path is refused.
    /// </summary>
    [TestMethod]
    public void SecureRootFallbackRefusesParentReference()
    {
        // Arrange
        using var outside = new TempDirectory();
        using var directory = new TempDirectory();

        Directory.CreateDirectory(Path.Combine(directory.Path, "sub"));
        outside.Write("secret.txt", "secret");

        using var root = SecureRoot.OpenWithoutKernelResolution(directory.Path);

        // Act and Assert
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("sub/../../secret.txt"), "parent reference");
        Assert.ThrowsExactly<SafeIoException>(() => root.ListNames("sub/..").ToList(), "parent reference in a listing");
        Assert.IsFalse(root.KernelResolves, "the fallback is active");
    }

    /// <summary>
    /// Directories, missing files and special files are refused.
    /// </summary>
    /// <param name="fallback">Whether paths are resolved without <c>openat2</c></param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SecureRootRefusesNonRegularFiles(bool fallback)
    {
        // Arrange
        using var directory = new TempDirectory();

        Directory.CreateDirectory(Path.Combine(directory.Path, "sub"));

        using var root = fallback ? SecureRoot.OpenWithoutKernelResolution(directory.Path) : SecureRoot.Open(directory.Path);

        // Act and Assert
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("sub"), "directory");
        Assert.ThrowsExactly<SafeIoException>(() => root.OpenRegular("missing"), "missing file");
        Assert.ThrowsExactly<SafeIoException>(() => SecureRoot.Open(Path.Combine(directory.Path, "missing")), "missing root");
    }

    #endregion // Methods
}