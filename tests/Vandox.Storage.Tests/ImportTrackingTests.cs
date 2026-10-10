using Vandox.Core.Model;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for the import state kept by <see cref="SqliteStore"/>
/// </summary>
[TestClass]
public class ImportTrackingTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Beginning an import creates the state once and returns it unchanged afterwards.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportCreatesStateOnce()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var changed = Samples.ImportStart(1);

        changed.Name = "other";
        changed.SourceType = "other-type";

        // Act
        var first = await store.BeginImportAsync(Samples.ImportStart(1), TestContext.CancellationToken);
        var again = await store.BeginImportAsync(changed, TestContext.CancellationToken);
        var second = await store.BeginImportAsync(Samples.ImportStart(2), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("logs/syslog.gz", first.Name, "name");
        Assert.AreEqual("syslog", first.FileName, "file name");
        Assert.AreEqual(Samples.Base, first.ModTime, "modification time");
        Assert.AreEqual(100L, first.Size, "size");
        Assert.AreEqual(0L, first.Records, "no records yet");
        Assert.IsFalse(first.Complete, "not complete");
        Assert.IsNull(first.CompletedAt, "no completion time");
        Assert.AreEqual(first.Id, again.Id, "the same state");
        Assert.AreEqual("syslog", again.SourceType, "the state is returned unchanged");
        Assert.AreNotEqual(first.Id, second.Id, "another content, another state");
    }

    /// <summary>
    /// A name is cut at a character boundary, and an unknown modification time is stored as unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportCutsLongNameAndKeepsUnknownModTime()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Samples.ImportStart(3);

        start.Name = new string('ä', StorageLimits.MaxImportNameBytes);
        start.ModTime = null;

        // Act
        var file = await store.BeginImportAsync(start, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(StorageLimits.MaxImportNameBytes / 2, file.Name.Length, "cut at a character boundary");
        Assert.IsNull(file.ModTime, "unknown modification time");
    }

    /// <summary>
    /// Imports that break a rule are refused.
    /// </summary>
    /// <param name="what">The rule that is broken</param>
    /// <param name="rule">The expected rule in the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("started missing", "started_at is required")]
    [DataRow("started not UTC", "started_at must be UTC")]
    [DataRow("started out of range", "started_at is outside the storable range")]
    [DataRow("sha short", "sha256 must be 32 bytes")]
    [DataRow("size negative", "size must not be negative")]
    [DataRow("file name long", "file name is longer than 1024 bytes")]
    [DataRow("type invalid", "source type must match ^[a-z][a-z0-9._-]{0,63}$")]
    public async Task SqliteStoreBeginImportRefusesInvalidStart(string what, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Samples.ImportStart(4);

        switch (what)
        {
            case "started missing":
                start.StartedAt = default;
                break;

            case "started not UTC":
                start.StartedAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(1));
                break;

            case "started out of range":
                start.StartedAt = new DateTimeOffset(1600, 1, 1, 0, 0, 0, TimeSpan.Zero);
                break;

            case "sha short":
                start.Sha256 = new byte[5];
                break;

            case "size negative":
                start.Size = -1;
                break;

            case "file name long":
                start.FileName = new string('a', StorageLimits.MaxImportNameBytes + 1);
                break;

            default:
                start.SourceType = "Bad Type";
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.BeginImportAsync(start, TestContext.CancellationToken), "invalid start");

        // Assert
        Assert.AreEqual($"store: invalid import: {rule}", exception.Message, "message");
    }

    /// <summary>
    /// A batch with an import step advances the stored count in the same transaction and completes the file.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchAdvancesAndCompletesImport()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var file = await store.BeginImportAsync(Samples.ImportStart(5), TestContext.CancellationToken);
        var first = Samples.Batch(Samples.Log(RecordOrigin.Import, 0, 0, "a"), Samples.Log(RecordOrigin.Import, 0, 1, "b"));
        var last = Samples.Batch();

        first.Import = new ImportStep
                       {
                           FileId = file.Id,
                           Done = 0
                       };
        last.Import = new ImportStep
                      {
                          FileId = file.Id,
                          Done = 2,
                          Complete = true
                      };

        // Act
        await store.WriteBatchAsync(first, TestContext.CancellationToken);

        var midway = await store.BeginImportAsync(Samples.ImportStart(5), TestContext.CancellationToken);

        await store.WriteBatchAsync(last, TestContext.CancellationToken);

        var done = await store.BeginImportAsync(Samples.ImportStart(5), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, midway.Records, "two records stored");
        Assert.IsFalse(midway.Complete, "not complete midway");
        Assert.IsTrue(done.Complete, "complete after the last batch");
        Assert.AreEqual(Samples.Base.AddMinutes(1), done.CompletedAt, "completion time is the receive time of the last batch");
    }

    /// <summary>
    /// A step that does not match the stored state, or a complete file, is refused and nothing is written.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreWriteBatchRefusesImportConflict()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var file = await store.BeginImportAsync(Samples.ImportStart(6), TestContext.CancellationToken);
        var wrong = Samples.Batch(Samples.Log(RecordOrigin.Import, 0, 0, "a"));
        var unknown = Samples.Batch(Samples.Log(RecordOrigin.Import, 0, 0, "a"));
        var done = Samples.Batch(Samples.Log(RecordOrigin.Import, 0, 0, "a"));

        wrong.Import = new ImportStep
                       {
                           FileId = file.Id,
                           Done = 5
                       };
        unknown.Import = new ImportStep
                         {
                             FileId = file.Id + 100,
                             Done = 0
                         };
        done.Import = new ImportStep
                      {
                          FileId = file.Id,
                          Done = 0,
                          Complete = true
                      };

        // Act
        var mismatch = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.WriteBatchAsync(wrong, TestContext.CancellationToken), "wrong count");
        var missing = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.WriteBatchAsync(unknown, TestContext.CancellationToken), "unknown file");

        await store.WriteBatchAsync(done, TestContext.CancellationToken);

        var complete = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.WriteBatchAsync(done, TestContext.CancellationToken), "complete file");
        var stored = await store.RecordsAsync(Samples.Query(RecordKind.LogLine), TestContext.CancellationToken);

        // Assert
        Assert.StartsWith("store: import state changed", mismatch.Message, "mismatch message");
        Assert.StartsWith("store: import state changed", missing.Message, "unknown file message");
        Assert.StartsWith("store: import state changed", complete.Message, "complete file message");
        Assert.HasCount(1, stored, "only the accepted batch wrote its record");
    }

    #endregion // Methods
}