using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

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

    /// <summary>
    /// Beginning an import takes the largest complete prefix of the same size and source type as the continuation, passes over the others and stores the head key.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportContinuesLargestCompletePrefix()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var small = await CompleteAsync(store, Start(10, 50, "small/syslog"));
        var large = await CompleteAsync(store, Start(11, 80, "large/syslog"));
        var wrongSize = await CompleteAsync(store, Start(14, 99, "wrong-size/syslog"));
        var wrongType = Start(13, 95, "other-type/mariadb");

        wrongType.SourceType = "mariadb";
        await CompleteAsync(store, wrongType);
        await store.BeginImportAsync(Start(12, 90, "incomplete/syslog"), TestContext.CancellationToken);

        var start = Start(1, 100, "grown/syslog");

        start.Prefixes = [Prefix(10, 50, 45), Prefix(11, 80, 75), Prefix(12, 90, 85), Prefix(13, 95, 90), Prefix(14, 98, 95), Prefix(15, 60, 55)];

        // Act
        var file = await store.BeginImportAsync(start, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(large.Id, file.ContinuesId, "continues the largest complete prefix");
        Assert.AreEqual(75L, file.Offset, "the offset of that prefix");
        Assert.AreEqual("large/syslog", file.ContinuedFrom, "the display name of the base");
        Assert.AreNotEqual(small.Id, file.ContinuesId, "a smaller prefix is passed over");
        Assert.AreNotEqual(wrongSize.Id, file.ContinuesId, "a prefix whose size differs from the stored size is passed over");
        Assert.AreEqual(0L, file.Records, "no records yet");
        Assert.IsFalse(file.Complete, "not complete");
    }

    /// <summary>
    /// Without a usable prefix the state is created as a whole: no continuation, offset 0, no display name.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportWithoutUsablePrefixStartsAsAWhole()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var other = Start(21, 40, "other/mariadb");
        var unusable = Start(2, 100, "unusable/syslog");
        var empty = Start(3, 100, "empty/syslog");

        other.SourceType = "mariadb";
        await CompleteAsync(store, other);
        await store.BeginImportAsync(Start(20, 30, "incomplete/syslog"), TestContext.CancellationToken);
        unusable.Prefixes = [Prefix(20, 30, 25), Prefix(21, 40, 35), Prefix(22, 50, 45)];

        // Act
        var withPrefixes = await store.BeginImportAsync(unusable, TestContext.CancellationToken);
        var withoutPrefixes = await store.BeginImportAsync(empty, TestContext.CancellationToken);

        // Assert
        Assert.IsNull(withPrefixes.ContinuesId, "no continuation although prefixes were given");
        Assert.AreEqual(0L, withPrefixes.Offset, "offset 0");
        Assert.AreEqual(string.Empty, withPrefixes.ContinuedFrom, "no display name");
        Assert.IsNull(withoutPrefixes.ContinuesId, "no continuation without prefixes");
        Assert.AreEqual(0L, withoutPrefixes.Offset, "offset 0 without prefixes");
        Assert.AreEqual(string.Empty, withoutPrefixes.ContinuedFrom, "no display name without prefixes");
    }

    /// <summary>
    /// An existing state is returned unchanged with its stored continuation, whatever the prefixes of the call are.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportReturnsStoredContinuationUnchanged()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var first = await CompleteAsync(store, Start(10, 50, "first/syslog"));
        var start = Start(1, 100, "grown/syslog");

        start.Prefixes = [Prefix(10, 50, 45)];

        var created = await store.BeginImportAsync(start, TestContext.CancellationToken);

        await CompleteAsync(store, Start(11, 80, "second/syslog"));

        var larger = Start(1, 100, "grown/syslog");

        larger.Prefixes = [Prefix(11, 80, 75)];

        var wholeAgain = Start(1, 100, "grown/syslog");

        // Act
        var withLarger = await store.BeginImportAsync(larger, TestContext.CancellationToken);
        var withNone = await store.BeginImportAsync(wholeAgain, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(first.Id, created.ContinuesId, "the first call continues the first base");
        Assert.AreEqual(created.Id, withLarger.Id, "the same state");
        Assert.AreEqual(first.Id, withLarger.ContinuesId, "a larger base that appeared later does not replace the stored one");
        Assert.AreEqual(45L, withLarger.Offset, "the stored offset");
        Assert.AreEqual("first/syslog", withLarger.ContinuedFrom, "the stored display name");
        Assert.AreEqual(first.Id, withNone.ContinuesId, "a call without prefixes returns the stored continuation");
        Assert.AreEqual(45L, withNone.Offset, "the stored offset without prefixes");
    }

    /// <summary>
    /// A stored head key makes a completed content a base for its head and source type.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportStoresHeadKey()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Start(1, 100, "a/syslog", 9);

        await CompleteAsync(store, start);

        // Act
        var found = await store.FindImportBasesAsync(Head(9), "syslog", TestContext.CancellationToken);
        var otherHead = await store.FindImportBasesAsync(Head(8), "syslog", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(1, found, "found with the stored head key");
        Assert.AreSequenceEqual(start.Sha256, found[0].Sha256, "the hash of the content");
        Assert.AreEqual(100L, found[0].Size, "the size of the content");
        Assert.IsEmpty(otherHead, "not found with another head key");
    }

    /// <summary>
    /// The lookup returns the complete contents of a head key and source type, largest first, and nothing else.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreFindImportBasesReturnsCompleteContentsLargestFirst()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var otherType = Start(4, 70, "other/mariadb");
        var otherHead = Start(5, 80, "head/syslog", 8);

        otherType.SourceType = "mariadb";
        await CompleteAsync(store, Start(1, 30, "small/syslog"));
        await CompleteAsync(store, Start(2, 90, "large/syslog"));
        await CompleteAsync(store, Start(3, 60, "middle/syslog"));
        await CompleteAsync(store, otherType);
        await CompleteAsync(store, otherHead);
        await store.BeginImportAsync(Start(6, 95, "incomplete/syslog"), TestContext.CancellationToken);

        // Act
        var found = await store.FindImportBasesAsync(Head(7), "syslog", TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("90,60,30", string.Join(',', found.Select(item => item.Size)), "complete contents of the head and type, largest first");
        Assert.AreSequenceEqual(Sum(2), found[0].Sha256, "the hash of the largest");
    }

    /// <summary>
    /// The lookup returns at most 64 contents, the largest ones.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreFindImportBasesReturnsAtMost64Largest()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        for (var size = 1; size <= 65; size++)
        {
            await CompleteAsync(store, Start(100 + size, size, $"file-{size}/syslog"));
        }

        // Act
        var found = await store.FindImportBasesAsync(Head(7), "syslog", TestContext.CancellationToken);

        // Assert
        Assert.HasCount(StorageLimits.MaxImportBases, found, "at most 64");
        Assert.AreEqual(65L, found[0].Size, "the largest first");
        Assert.AreEqual(2L, found[^1].Size, "the smallest of the 64 largest; size 1 is left out");
    }

    /// <summary>
    /// Starts and lookups that break a rule are refused with the exact message, and nothing is written.
    /// </summary>
    /// <param name="what">The rule that is broken</param>
    /// <param name="rule">The expected rule in the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("head 31", "head_sha256 must be 32 bytes")]
    [DataRow("head 33", "head_sha256 must be 32 bytes")]
    [DataRow("prefixes 65", "at most 64 prefixes")]
    [DataRow("prefix sha short", "prefix sha256 must be 32 bytes")]
    [DataRow("prefix size zero", "prefix size must be positive and smaller than size")]
    [DataRow("prefix size equal", "prefix size must be positive and smaller than size")]
    [DataRow("prefix size larger", "prefix size must be positive and smaller than size")]
    [DataRow("prefix offset zero", "prefix offset must be positive and at most the prefix size")]
    [DataRow("prefix offset beyond", "prefix offset must be positive and at most the prefix size")]
    public async Task SqliteStoreBeginImportRefusesInvalidHeadAndPrefixes(string what, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Start(1, 100, "refused/syslog");

        switch (what)
        {
            case "head 31":
                start.HeadSha256 = new byte[31];
                break;

            case "head 33":
                start.HeadSha256 = new byte[33];
                break;

            case "prefixes 65":
                start.Prefixes = Enumerable.Range(1, 65).Select(number => Prefix(number, 50, 40)).ToList();
                break;

            case "prefix sha short":
                start.Prefixes = [Prefix(1, 50, 40, new byte[31])];
                break;

            case "prefix size zero":
                start.Prefixes = [Prefix(1, 0, 0)];
                break;

            case "prefix size equal":
                start.Prefixes = [Prefix(1, 100, 90)];
                break;

            case "prefix size larger":
                start.Prefixes = [Prefix(1, 101, 90)];
                break;

            case "prefix offset zero":
                start.Prefixes = [Prefix(1, 50, 0)];
                break;

            default:
                start.Prefixes = [Prefix(1, 50, 51)];
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.BeginImportAsync(start, TestContext.CancellationToken), "invalid start");
        var valid = Start(1, 100, "valid/syslog");
        var created = await store.BeginImportAsync(valid, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual($"store: invalid import: {rule}", exception.Message, "message");
        Assert.AreEqual("valid/syslog", created.Name, "the refused start wrote nothing");
    }

    /// <summary>
    /// A prefix that is as large as allowed and has its offset at its end is accepted.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportAcceptsPrefixesAtTheirLimits()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var start = Start(1, 100, "grown/syslog");

        start.Prefixes = Enumerable.Range(1, StorageLimits.MaxImportBases).Select(number => Prefix(number, 99, 99)).ToList();

        // Act
        var file = await store.BeginImportAsync(start, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("grown/syslog", file.Name, "64 prefixes, the largest size allowed and the largest offset allowed are accepted");
    }

    /// <summary>
    /// A lookup with a head key that is not 32 bytes or a source type that is not valid is refused.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreFindImportBasesRefusesInvalidLookup()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var shortHead = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.FindImportBasesAsync(new byte[31], "syslog", TestContext.CancellationToken), "head of 31 bytes");
        var longHead = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.FindImportBasesAsync(new byte[33], "syslog", TestContext.CancellationToken), "head of 33 bytes");
        var badType = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.FindImportBasesAsync(new byte[32], "Bad Type", TestContext.CancellationToken), "invalid source type");

        // Assert
        Assert.AreEqual("store: invalid import: head_sha256 must be 32 bytes", shortHead.Message, "short head message");
        Assert.AreEqual("store: invalid import: head_sha256 must be 32 bytes", longHead.Message, "long head message");
        Assert.AreEqual("store: invalid import: source type must match ^[a-z][a-z0-9._-]{0,63}$", badType.Message, "source type message");
    }

    /// <summary>
    /// A lookup that the database cannot answer is reported as a store error with the fixed prefix.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreFindImportBasesReportsDatabaseFailureAsStoreException()
    {
        // Arrange
        using var directory = new TempDirectory();

        var created = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await created.DisposeAsync();

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, "vandox.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.CancellationToken);

            using var command = connection.CreateCommand();

            command.CommandText = "DROP INDEX import_files_head; ALTER TABLE import_files DROP COLUMN head_sha256";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        // Act
        var error = await Assert.ThrowsExactlyAsync<StoreException>(() => store.FindImportBasesAsync(new byte[32], "syslog", TestContext.CancellationToken), "lookup on a broken table");

        // Assert
        Assert.StartsWith("store: finding import bases: ", error.Message, "message prefix");
        Assert.IsInstanceOfType<SqliteException>(error.InnerException, "inner exception");
    }

    /// <summary>
    /// A content that is the start of a complete stored content, up to one of its line ends, is recognized: no state is created, and the result names the container.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportRecognizesStartOfCompleteContentAtLineEnd()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var newer = Lines("a", 10);
        var older = newer[..LineEnd(4)];
        var container = await ImportAsync(store, newer, "logs/new.log");

        // Act
        var contained = await store.BeginImportAsync(ContentStart(older, "logs/old.log"), TestContext.CancellationToken);
        var again = await store.BeginImportAsync(ContentStart(older, "logs/old.log"), TestContext.CancellationToken);
        var existing = await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("logs/new.log", contained.ContainedIn, "the display name of the container");
        Assert.AreEqual(0L, contained.Id, "a contained result carries no ID");
        Assert.IsFalse(contained.Complete, "a contained result is not complete");
        Assert.AreEqual(0L, contained.Attempt, "a contained result has attempt 0");
        Assert.AreEqual(0L, contained.Records, "a contained result has no records");
        Assert.AreEqual("logs/new.log", again.ContainedIn, "the same result in a later call");
        Assert.AreEqual("1", await ScalarAsync(path, "SELECT count(*) FROM import_files"), "no state was created for the contained content");
        Assert.AreEqual(string.Empty, existing.ContainedIn, "an existing state has no container");
        Assert.AreEqual(container.Id, existing.Id, "the existing state is the container itself");
        Assert.IsTrue(existing.Complete, "the container is complete");
    }

    /// <summary>
    /// A start of a complete content that ends on a line end is recognized wherever the line end lies in the rows of line hashes, in particular at the end of a row and one line end after it.
    /// </summary>
    /// <param name="lines">The number of lines of the older content</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(6)]
    [DataRow(7)]
    [DataRow(9)]
    public async Task SqliteStoreBeginImportRecognizesStartAcrossRowBoundaries(int lines)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var newer = Lines("a", 10);

        await ImportAsync(store, newer, "logs/new.log", perRow: 3);

        // Act
        var contained = await store.BeginImportAsync(ContentStart(newer[..LineEnd(lines)], "logs/old.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("logs/new.log", contained.ContainedIn, $"{lines} lines are the start of the container");
    }

    /// <summary>
    /// Nothing is recognized, and the content is imported as a whole, when it is not exactly the start of a complete content of the same type and head key that has line hashes of its final attempt.
    /// </summary>
    /// <param name="what">The case</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("ends inside a line")]
    [DataRow("one byte differs")]
    [DataRow("other source type")]
    [DataRow("other head key")]
    [DataRow("container incomplete")]
    [DataRow("container without line hashes")]
    [DataRow("rows of another attempt")]
    [DataRow("longer than the other content")]
    public async Task SqliteStoreBeginImportDoesNotRecognizeWhatIsNotAStartOfACompleteContent(string what)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var newer = Lines("a", 10);
        var older = newer[..LineEnd(4)];
        var start = ContentStart(older, "logs/old.log");

        switch (what)
        {
            case "ends inside a line":
                start = ContentStart(newer[..(LineEnd(4) - 3)], "logs/old.log");
                await ImportAsync(store, newer, "logs/new.log");
                break;

            case "one byte differs":
                older[LineEnd(2) + 2] ^= 1;
                start = ContentStart(older, "logs/old.log");
                await ImportAsync(store, newer, "logs/new.log");
                break;

            case "other source type":
                start.SourceType = "mariadb";
                await ImportAsync(store, newer, "logs/new.log");
                break;

            case "other head key":
                start.HeadSha256 = Head(8);
                await ImportAsync(store, newer, "logs/new.log");
                break;

            case "container incomplete":
                {
                    var file = await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);

                    foreach (var row in Rows(newer))
                    {
                        row.FileId = file.Id;
                        row.Attempt = file.Attempt;
                        await store.AddLineHashesAsync(row, TestContext.CancellationToken);
                    }
                }
                break;

            case "container without line hashes":
                await ImportAsync(store, newer, "logs/new.log", withHashes: false);
                break;

            case "rows of another attempt":
                {
                    var file = await ImportAsync(store, newer, "logs/new.log");

                    await ExecuteAsync(path, "DELETE FROM import_line_hashes");

                    foreach (var row in Rows(newer))
                    {
                        row.FileId = file.Id;
                        row.Attempt = file.Attempt + 1;
                        await InsertRowAsync(path, row);
                    }
                }
                break;

            default:
                start = ContentStart(Lines("a", 12), "logs/old.log");
                await ImportAsync(store, newer, "logs/new.log");
                break;
        }

        // Act
        var file2 = await store.BeginImportAsync(start, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, file2.ContainedIn, $"{what}: not contained");
        Assert.IsGreaterThan(0L, file2.Id, $"{what}: a state was created");
        Assert.AreEqual(1L, file2.Attempt, $"{what}: a new state has attempt 1");
        Assert.AreEqual("2", await ScalarAsync(path, "SELECT count(*) FROM import_files"), $"{what}: the content has its own row");
    }

    /// <summary>
    /// A content with the hash of a complete stored content keeps today's result even when it is the start of a larger complete content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportOfStoredCompleteContentIsNotContained()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var newer = Lines("a", 10);
        var older = newer[..LineEnd(5)];
        var first = await ImportAsync(store, older, "logs/old.log");

        await ImportAsync(store, newer, "logs/new.log");

        // Act
        var again = await store.BeginImportAsync(ContentStart(older, "logs/other.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, again.ContainedIn, "the stored state wins over the containment lookup");
        Assert.AreEqual(first.Id, again.Id, "the stored state");
        Assert.IsTrue(again.Complete, "it is complete");
        Assert.AreEqual(1L, again.Attempt, "a complete state is returned unchanged");
    }

    /// <summary>
    /// A content with the hash of an incomplete stored content is resumed with its attempt raised even when it is the start of a larger complete content.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportOfStoredIncompleteContentIsResumedNotContained()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var newer = Lines("a", 10);
        var older = newer[..LineEnd(5)];
        var first = await store.BeginImportAsync(ContentStart(older, "logs/old.log"), TestContext.CancellationToken);

        await ImportAsync(store, newer, "logs/new.log");

        // Act
        var again = await store.BeginImportAsync(ContentStart(older, "logs/old.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(string.Empty, again.ContainedIn, "the stored state wins over the containment lookup");
        Assert.AreEqual(first.Id, again.Id, "the stored state");
        Assert.IsFalse(again.Complete, "it is not complete");
        Assert.AreEqual(2L, again.Attempt, "the attempt is raised");
    }

    /// <summary>
    /// Containment wins over a forward continuation: a content that continues a smaller base and is the start of a complete content is contained.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportPrefersContainmentOverForwardBase()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var newer = Lines("a", 10);
        var smaller = newer[..LineEnd(3)];
        var older = newer[..LineEnd(6)];

        await ImportAsync(store, smaller, "logs/small.log");
        await ImportAsync(store, newer, "logs/new.log");

        var start = ContentStart(older, "logs/old.log");

        start.Prefixes = [Prefix(0, smaller.Length, smaller.Length, SHA256.HashData(smaller))];

        // Act
        var result = await store.BeginImportAsync(start, TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("logs/new.log", result.ContainedIn, "contained, not continued");
        Assert.IsNull(result.ContinuesId, "no continuation");
        Assert.AreEqual("2", await ScalarAsync(path, "SELECT count(*) FROM import_files"), "no state for the contained content");
    }

    /// <summary>
    /// Only the 64 largest complete contents of the head key and source type that are larger than the content are compared; a container that ranks 65th is not found.
    /// </summary>
    /// <param name="decoys">The number of larger contents that are not a container</param>
    /// <param name="found">Whether the container is found</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(StorageLimits.MaxImportContainers - 1, true)]
    [DataRow(StorageLimits.MaxImportContainers, false)]
    public async Task SqliteStoreBeginImportComparesOnlyThe64LargestContainers(int decoys, bool found)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var container = Lines("a", 5);
        var older = container[..LineEnd(4)];

        await ImportAsync(store, container, "logs/container.log");

        for (var index = 0; index < decoys; index++)
        {
            await ImportAsync(store, Lines($"decoy{index:D2}", 6), $"logs/decoy-{index}.log");
        }

        // Act
        var result = await store.BeginImportAsync(ContentStart(older, "logs/old.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(found ? "logs/container.log" : string.Empty, result.ContainedIn, $"{decoys} larger contents");
    }

    /// <summary>
    /// A content that was imported again after it was begun again is recognized by the line hashes of the attempt that completed it.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportRecognizesStartOfContentCompletedByLaterAttempt()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var newer = Lines("a", 10);
        var first = await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);
        var half = Rows(newer, 3)[0];

        half.FileId = first.Id;
        half.Attempt = first.Attempt;
        await store.AddLineHashesAsync(half, TestContext.CancellationToken);

        var second = await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);

        foreach (var row in Rows(newer, 3))
        {
            row.FileId = second.Id;
            row.Attempt = second.Attempt;
            await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        }

        await WriteStepAsync(store, Step(second.Id, 0, second.Attempt, true));

        // Act
        var contained = await store.BeginImportAsync(ContentStart(newer[..LineEnd(8)], "logs/old.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, second.Attempt, "the second attempt");
        Assert.AreEqual("logs/new.log", contained.ContainedIn, "recognized");
        Assert.AreEqual("2", await ScalarAsync(path, "SELECT hashes_attempt FROM import_files WHERE id = " + second.Id.ToString(CultureInfo.InvariantCulture)), "the completing attempt is the line hashes attempt");
        Assert.AreEqual("0", await ScalarAsync(path, "SELECT count(*) FROM import_line_hashes WHERE attempt <> 2"), "no row of the first attempt is left");
    }

    /// <summary>
    /// A new state starts at attempt 1, every call for an incomplete state adds one, and a complete state is returned unchanged.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportCountsAttemptsOfIncompleteStateOnly()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 4);

        // Act
        var first = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var second = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var third = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var row = Rows(content)[0];

        row.FileId = third.Id;
        row.Attempt = third.Attempt;
        await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        await WriteStepAsync(store, Step(third.Id, 0, third.Attempt, true));

        var complete = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var completeAgain = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(1L, first.Attempt, "a new state");
        Assert.AreEqual(2L, second.Attempt, "begun again");
        Assert.AreEqual(3L, third.Attempt, "begun a third time");
        Assert.AreEqual(3L, complete.Attempt, "a complete state is returned unchanged");
        Assert.AreEqual(3L, completeAgain.Attempt, "and stays unchanged");
        Assert.AreEqual("1", await ScalarAsync(path, "SELECT count(*) FROM import_line_hashes"), "a complete state deletes nothing when it is returned");
    }

    /// <summary>
    /// The completing batch sets the line hashes attempt and deletes the line hash rows of every other attempt of the file in the same transaction.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreCompletingBatchKeepsRowsOfItsAttemptOnly()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 10);
        var file = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var rows = Rows(content, 4);
        var other = await store.BeginImportAsync(ContentStart(Lines("b", 3), "logs/b.log"), TestContext.CancellationToken);

        foreach (var row in rows)
        {
            row.FileId = file.Id;
            row.Attempt = file.Attempt;
            await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        }

        await InsertRowAsync(path, Row(file.Id, 9, 12));
        await InsertRowAsync(path, Row(other.Id, 1, 12));

        // Act
        await WriteStepAsync(store, Step(file.Id, 0, file.Attempt, true));

        // Assert
        var id = file.Id.ToString(CultureInfo.InvariantCulture);

        Assert.AreEqual("1", await ScalarAsync(path, $"SELECT hashes_attempt FROM import_files WHERE id = {id}"), "the line hashes attempt");
        Assert.AreEqual("0", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id} AND attempt <> 1"), "rows of another attempt are deleted");
        Assert.AreEqual(rows.Count.ToString(CultureInfo.InvariantCulture), await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id} AND attempt = 1"), "rows of the completing attempt are kept");
        Assert.AreEqual("1", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {other.Id}"), "rows of another file are untouched");
    }

    /// <summary>
    /// A batch with attempt 0 is not checked against the attempt of the file, and completing the file with it leaves no line hashes and no line hashes attempt.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBatchWithAttemptZeroCompletesWithoutLineHashes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var newer = Lines("a", 10);

        await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);

        var file = await store.BeginImportAsync(ContentStart(newer, "logs/new.log"), TestContext.CancellationToken);

        foreach (var row in Rows(newer))
        {
            row.FileId = file.Id;
            row.Attempt = file.Attempt;
            await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        }

        // Act
        await WriteStepAsync(store, Step(file.Id, 0, 0, false), Samples.Log(RecordOrigin.Import, 0, 0, "x"));
        await WriteStepAsync(store, Step(file.Id, 1, 0, true));

        var contained = await store.BeginImportAsync(ContentStart(newer[..LineEnd(4)], "logs/old.log"), TestContext.CancellationToken);

        // Assert
        var id = file.Id.ToString(CultureInfo.InvariantCulture);

        Assert.AreEqual(2L, file.Attempt, "the file has attempt 2");
        Assert.AreEqual("-1", await ScalarAsync(path, $"SELECT coalesce(hashes_attempt, -1) FROM import_files WHERE id = {id}"), "no line hashes attempt");
        Assert.AreEqual("0", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}"), "every row of the file is deleted");
        Assert.AreEqual(string.Empty, contained.ContainedIn, "the file is never a container");
    }

    /// <summary>
    /// Line hashes of an attempt that never completes do not pile up: beginning the content again deletes the rows of every other attempt of that file and leaves other files alone.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreBeginImportDeletesLineHashesOfEarlierAttempts()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 10);
        var otherContent = Lines("b", 10);
        var other = await store.BeginImportAsync(ContentStart(otherContent, "logs/b.log"), TestContext.CancellationToken);
        var otherRow = Rows(otherContent, 4)[0];

        otherRow.FileId = other.Id;
        otherRow.Attempt = other.Attempt;
        await store.AddLineHashesAsync(otherRow, TestContext.CancellationToken);

        var first = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var id = first.Id.ToString(CultureInfo.InvariantCulture);

        await AddRowsAsync(store, first, content, 4, 2);

        // Act
        var second = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var afterSecond = await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}");

        await AddRowsAsync(store, second, content, 4, 1);

        var third = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var afterThird = await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}");

        await AddRowsAsync(store, third, content, 4, 3);
        await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);

        var fourth = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual(2L, second.Attempt, "second attempt");
        Assert.AreEqual(3L, third.Attempt, "third attempt");
        Assert.AreEqual(5L, fourth.Attempt, "fifth attempt");
        Assert.AreEqual("0", afterSecond, "no row of attempt 1 is left");
        Assert.AreEqual("0", afterThird, "no row of attempts 1 and 2 is left");
        Assert.AreEqual("0", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}"), "after any number of calls only rows of the current attempt can exist");
        Assert.AreEqual("1", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {other.Id}"), "rows of other files are untouched");
    }

    /// <summary>
    /// Rows of line hashes that break a rule are refused with the exact message.
    /// </summary>
    /// <param name="what">The rule that is broken</param>
    /// <param name="rule">The expected rule in the message</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow("file ID", "file id must be positive")]
    [DataRow("attempt", "attempt must be positive")]
    [DataRow("first offset", "first offset must not be negative")]
    [DataRow("last offset equals first", "last offset must be greater than the first offset")]
    [DataRow("last offset below first", "last offset must be greater than the first offset")]
    [DataRow("no hashes", "line hashes must be 1 to 4096 hashes of 16 bytes")]
    [DataRow("partial hash", "line hashes must be 1 to 4096 hashes of 16 bytes")]
    [DataRow("short hash", "line hashes must be 1 to 4096 hashes of 16 bytes")]
    [DataRow("4097 hashes", "line hashes must be 1 to 4096 hashes of 16 bytes")]
    [DataRow("more hashes than bytes", "line hashes must not outnumber the bytes they cover")]
    public async Task SqliteStoreAddLineHashesRefusesInvalidRow(string what, string rule)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var file = await store.BeginImportAsync(ContentStart(Lines("a", 3), "logs/a.log"), TestContext.CancellationToken);
        var row = Row(file.Id, file.Attempt, 36, new byte[32]);

        switch (what)
        {
            case "file ID":
                row.FileId = 0;
                break;

            case "attempt":
                row.Attempt = 0;
                break;

            case "first offset":
                row.FirstOffset = -1;
                break;

            case "last offset equals first":
                row.FirstOffset = 36;
                break;

            case "last offset below first":
                row.FirstOffset = 40;
                break;

            case "no hashes":
                row.Hashes = [];
                break;

            case "partial hash":
                row.Hashes = new byte[17];
                break;

            case "short hash":
                row.Hashes = new byte[15];
                break;

            case "4097 hashes":
                row.LastOffset = 100000;
                row.Hashes = new byte[(StorageLimits.MaxLineHashes + 1) * StorageLimits.LineHashBytes];
                break;

            default:
                row.LastOffset = 1;
                break;
        }

        // Act
        var exception = await Assert.ThrowsExactlyAsync<InvalidImportException>(() => store.AddLineHashesAsync(row, TestContext.CancellationToken), "invalid row");

        // Assert
        Assert.AreEqual($"store: invalid import: {rule}", exception.Message, "message");
        Assert.AreEqual("0", await ScalarAsync(path, "SELECT count(*) FROM import_line_hashes"), "nothing was written");
    }

    /// <summary>
    /// A row at the limits is stored: the largest number of hashes, and as many hashes as bytes.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreAddLineHashesStoresRowsAtTheirLimits()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var file = await store.BeginImportAsync(ContentStart(Lines("a", 3), "logs/a.log"), TestContext.CancellationToken);

        // Act
        await store.AddLineHashesAsync(Row(file.Id, file.Attempt, StorageLimits.MaxLineHashes, new byte[StorageLimits.MaxLineHashes * StorageLimits.LineHashBytes]), TestContext.CancellationToken);
        await store.AddLineHashesAsync(Row(file.Id, file.Attempt, StorageLimits.MaxLineHashes + 1, new byte[StorageLimits.LineHashBytes], StorageLimits.MaxLineHashes), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual("2", await ScalarAsync(path, "SELECT count(*) FROM import_line_hashes"), "both rows are stored");
        Assert.AreEqual("65536", await ScalarAsync(path, "SELECT max(length(hashes)) FROM import_line_hashes"), "4096 hashes of 16 bytes");
    }

    /// <summary>
    /// A row is refused with a conflict when the file is complete, unknown or was begun again, and a second row with the same file, attempt and last offset is a store error.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreAddLineHashesRefusesConflictsAndDuplicates()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 3);
        var complete = await ImportAsync(store, content, "logs/complete.log");
        var open = await store.BeginImportAsync(ContentStart(Lines("b", 3), "logs/open.log"), TestContext.CancellationToken);
        var row = Rows(content)[0];

        // Act
        row.FileId = complete.Id;
        row.Attempt = complete.Attempt;

        var onComplete = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.AddLineHashesAsync(row, TestContext.CancellationToken), "complete file");

        row.FileId = open.Id + 100;
        row.Attempt = 1;

        var onUnknown = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.AddLineHashesAsync(row, TestContext.CancellationToken), "unknown file");

        row.FileId = open.Id;
        row.Attempt = open.Attempt + 1;

        var onOtherAttempt = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.AddLineHashesAsync(row, TestContext.CancellationToken), "other attempt");

        row.Attempt = open.Attempt;
        await store.AddLineHashesAsync(row, TestContext.CancellationToken);

        var duplicate = await Assert.ThrowsExactlyAsync<StoreException>(() => store.AddLineHashesAsync(row, TestContext.CancellationToken), "duplicate row");

        // Assert
        Assert.AreEqual($"store: import state changed: file {complete.Id} is complete, unknown or was begun again", onComplete.Message, "complete file message");
        Assert.AreEqual($"store: import state changed: file {open.Id + 100} is complete, unknown or was begun again", onUnknown.Message, "unknown file message");
        Assert.AreEqual($"store: import state changed: file {open.Id} is complete, unknown or was begun again", onOtherAttempt.Message, "other attempt message");
        Assert.StartsWith("store: adding line hashes: ", duplicate.Message, "duplicate message prefix");
        Assert.IsInstanceOfType<SqliteException>(duplicate.InnerException, "inner exception");
        Assert.AreEqual("1", await ScalarAsync(path, "SELECT count(*) FROM import_line_hashes WHERE file_id = " + open.Id.ToString(CultureInfo.InvariantCulture)), "only the first row is stored");
    }

    /// <summary>
    /// A batch that carries an attempt which is not the file's current one is refused with the exact message and changes nothing, whether it completes the file or not.
    /// </summary>
    /// <param name="complete">Whether the batch completes the file</param>
    /// <param name="attempt">The attempt of the batch</param>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    [DataRow(false, 1L)]
    [DataRow(true, 1L)]
    [DataRow(true, 3L)]
    public async Task SqliteStoreWriteBatchRefusesAttemptThatIsNotTheCurrentOne(bool complete, long attempt)
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 4);

        await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);

        var file = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var row = Rows(content)[0];

        row.FileId = file.Id;
        row.Attempt = file.Attempt;
        await store.AddLineHashesAsync(row, TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => WriteStepAsync(store, Step(file.Id, 0, attempt, complete), Samples.Log(RecordOrigin.Import, 0, 0, "x")), "other attempt");

        // Assert
        var id = file.Id.ToString(CultureInfo.InvariantCulture);

        Assert.AreEqual($"store: import state changed: file {file.Id} does not have 0 records stored, is complete or was begun again", exception.Message, "message");
        Assert.AreEqual("0", await ScalarAsync(path, $"SELECT records FROM import_files WHERE id = {id}"), "no record counted");
        Assert.AreEqual("0", await ScalarAsync(path, $"SELECT complete FROM import_files WHERE id = {id}"), "not complete");
        Assert.AreEqual("-1", await ScalarAsync(path, $"SELECT coalesce(hashes_attempt, -1) FROM import_files WHERE id = {id}"), "no line hashes attempt");
        Assert.AreEqual("1", await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}"), "no row deleted");
        Assert.AreEqual("0", await ScalarAsync(path, "SELECT count(*) FROM log_lines"), "the batch stored no record");
    }

    /// <summary>
    /// A run that another run superseded cannot write line hashes or a batch, nothing of it is stored, and the run that superseded it completes the file and makes it a container.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task SqliteStoreSupersededRunCannotWriteAndLaterRunCompletes()
    {
        // Arrange
        using var directory = new TempDirectory();
        await using var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);
        var path = DatabasePath(directory);
        var content = Lines("a", 10);
        var runA = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var runB = await store.BeginImportAsync(ContentStart(content, "logs/a.log"), TestContext.CancellationToken);
        var rows = Rows(content, 4);
        var id = runB.Id.ToString(CultureInfo.InvariantCulture);

        await AddRowsAsync(store, runB, content, 4, 2);

        var staleRow = rows[2];

        staleRow.FileId = runA.Id;
        staleRow.Attempt = runA.Attempt;

        // Act
        var onLineHashes = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => store.AddLineHashesAsync(staleRow, TestContext.CancellationToken), "line hashes of run A");
        var onBatch = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => WriteStepAsync(store, Step(runA.Id, 0, runA.Attempt, false), Samples.Log(RecordOrigin.Import, 0, 0, "x")), "batch of run A");
        var onCompleting = await Assert.ThrowsExactlyAsync<ImportConflictException>(() => WriteStepAsync(store, Step(runA.Id, 0, runA.Attempt, true)), "completing batch of run A");
        var afterConflicts = await ScalarAsync(path, $"SELECT count(*) FROM import_line_hashes WHERE file_id = {id}");
        var state = await ScalarAsync(path, $"SELECT records || ',' || complete || ',' || coalesce(hashes_attempt, -1) FROM import_files WHERE id = {id}");

        foreach (var row in rows.Skip(2))
        {
            row.FileId = runB.Id;
            row.Attempt = runB.Attempt;
            await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        }

        await WriteStepAsync(store, Step(runB.Id, 0, runB.Attempt, true));

        var contained = await store.BeginImportAsync(ContentStart(content[..LineEnd(9)], "logs/old.log"), TestContext.CancellationToken);

        // Assert
        Assert.AreEqual($"store: import state changed: file {runA.Id} is complete, unknown or was begun again", onLineHashes.Message, "line hashes message");
        Assert.AreEqual($"store: import state changed: file {runA.Id} does not have 0 records stored, is complete or was begun again", onBatch.Message, "batch message");
        Assert.AreEqual(onBatch.Message, onCompleting.Message, "completing batch message");
        Assert.AreEqual("2", afterConflicts, "only the rows of run B exist and none was deleted");
        Assert.AreEqual("0,0,-1", state, "records, complete and line hashes attempt unchanged");
        Assert.AreEqual("logs/a.log", contained.ContainedIn, "the content is a container after run B completed it");
    }

    /// <summary>
    /// Returns a 32-byte hash that differs for every seed.
    /// </summary>
    /// <param name="seed">The seed</param>
    /// <returns>The hash</returns>
    private static byte[] Sum(int seed)
    {
        var bytes = new byte[32];

        BitConverter.GetBytes(seed).CopyTo(bytes, 0);
        bytes[31] = 0xAB;

        return bytes;
    }

    /// <summary>
    /// Returns a 32-byte head key.
    /// </summary>
    /// <param name="seed">The byte it is filled with</param>
    /// <returns>The head key</returns>
    private static byte[] Head(byte seed)
    {
        return Enumerable.Repeat(seed, 32).ToArray();
    }

    /// <summary>
    /// Creates the start of a syslog import.
    /// </summary>
    /// <param name="seed">Makes the hash of the content unique</param>
    /// <param name="size">The size of the content</param>
    /// <param name="name">The display path</param>
    /// <param name="head">The byte the head key is filled with</param>
    /// <returns>The start</returns>
    private static ImportFileStart Start(int seed, long size, string name, byte head = 7)
    {
        return new ImportFileStart
               {
                   Sha256 = Sum(seed),
                   Size = size,
                   Name = name,
                   FileName = "syslog",
                   ModTime = Samples.Base,
                   SourceType = "syslog",
                   StartedAt = Samples.Base,
                   HeadSha256 = Head(head)
               };
    }

    /// <summary>
    /// Creates a prefix.
    /// </summary>
    /// <param name="seed">The seed of the hash of the base</param>
    /// <param name="size">The size of the base</param>
    /// <param name="offset">The continuation point</param>
    /// <param name="sum">The hash of the base; derived from <paramref name="seed"/> when <c>null</c></param>
    /// <returns>The prefix</returns>
    private static ImportPrefix Prefix(int seed, long size, long offset, byte[]? sum = null)
    {
        return new ImportPrefix
               {
                   Sha256 = sum ?? Sum(seed),
                   Size = size,
                   Offset = offset
               };
    }

    /// <summary>
    /// Creates the start of an import of a content, with its real SHA-256 and size.
    /// </summary>
    /// <param name="content">The content</param>
    /// <param name="name">The display path</param>
    /// <param name="type">The source type</param>
    /// <returns>The start</returns>
    private static ImportFileStart ContentStart(byte[] content, string name, string type = "syslog")
    {
        var start = Start(0, content.Length, name);

        start.Sha256 = SHA256.HashData(content);
        start.SourceType = type;

        return start;
    }

    /// <summary>
    /// Creates a content of lines of 12 bytes each.
    /// </summary>
    /// <param name="tag">A text that makes the lines differ between contents; 1 character</param>
    /// <param name="count">The number of lines</param>
    /// <returns>The content</returns>
    private static byte[] Lines(string tag, int count)
    {
        var text = new StringBuilder();

        for (var index = 0; index < count; index++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{tag} line {index:D4}\n");
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>
    /// Returns the offset after the given number of lines of a content made with <see cref="Lines"/> and tag of 1 character.
    /// </summary>
    /// <param name="count">The number of lines</param>
    /// <returns>The offset</returns>
    private static int LineEnd(int count)
    {
        return count * 12;
    }

    /// <summary>
    /// Computes the rows of line hashes of a content: the first 16 bytes of the SHA-256 of the bytes up to every line end.
    /// </summary>
    /// <param name="content">The content</param>
    /// <param name="perRow">The number of hashes per row</param>
    /// <returns>The rows without file ID and attempt</returns>
    private static List<ImportLineHashes> Rows(byte[] content, int perRow = StorageLimits.MaxLineHashes)
    {
        var ends = new List<long>();
        var hashes = new List<byte[]>();
        var from = 0;

        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            for (var index = 0; index < content.Length; index++)
            {
                if (content[index] != (byte)'\n')
                {
                    continue;
                }

                hash.AppendData(content, from, index + 1 - from);
                from = index + 1;
                ends.Add(from);
                hashes.Add(hash.GetCurrentHash()[..StorageLimits.LineHashBytes]);
            }
        }

        return Enumerable.Range(0, ends.Count)
                         .Chunk(perRow)
                         .Select(chunk => Row(0, 0, ends[chunk[^1]], chunk.SelectMany(index => hashes[index]).ToArray(), chunk[0] == 0 ? 0 : ends[chunk[0] - 1]))
                         .ToList();
    }

    /// <summary>
    /// Returns the path of the database file of a store directory.
    /// </summary>
    /// <param name="directory">The directory</param>
    /// <returns>The path</returns>
    private static string DatabasePath(TempDirectory directory)
    {
        return Path.Combine(directory.Path, StorageLimits.FileName);
    }

    /// <summary>
    /// Creates an import step without records.
    /// </summary>
    /// <param name="fileId">The file ID</param>
    /// <param name="done">The number of records stored before the step</param>
    /// <param name="attempt">The attempt</param>
    /// <param name="complete">Whether the step completes the file</param>
    /// <returns>The step</returns>
    private static ImportStep Step(long fileId, long done, long attempt, bool complete)
    {
        return new ImportStep
               {
                   FileId = fileId,
                   Done = done,
                   Attempt = attempt,
                   Complete = complete
               };
    }

    /// <summary>
    /// Creates a row of line hashes; zeros unless given.
    /// </summary>
    /// <param name="fileId">The file ID</param>
    /// <param name="attempt">The attempt</param>
    /// <param name="last">The offset of the last line end</param>
    /// <param name="hashes">The hashes; one hash of zeros when <c>null</c></param>
    /// <param name="first">The offset the row starts at</param>
    /// <returns>The row</returns>
    private static ImportLineHashes Row(long fileId, long attempt, long last, byte[]? hashes = null, long first = 0)
    {
        return new ImportLineHashes
               {
                   FileId = fileId,
                   Attempt = attempt,
                   FirstOffset = first,
                   LastOffset = last,
                   Hashes = hashes ?? new byte[StorageLimits.LineHashBytes]
               };
    }

    /// <summary>
    /// Begins an import and completes it without records.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="start">The start</param>
    /// <returns>A task that returns the state as it was created</returns>
    private async Task<ImportFile> CompleteAsync(SqliteStore store, ImportFileStart start)
    {
        var file = await store.BeginImportAsync(start, TestContext.CancellationToken);
        var batch = Samples.Batch();

        batch.Import = new ImportStep
                       {
                           FileId = file.Id,
                           Done = 0,
                           Complete = true
                       };
        await store.WriteBatchAsync(batch, TestContext.CancellationToken);

        return file;
    }

    /// <summary>
    /// Adds rows of line hashes of a content for the attempt of a state.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="file">The state</param>
    /// <param name="content">The content</param>
    /// <param name="perRow">The number of hashes per row</param>
    /// <param name="count">The number of rows to add, from the first</param>
    /// <returns>A task that completes when the rows are added</returns>
    private async Task AddRowsAsync(SqliteStore store, ImportFile file, byte[] content, int perRow, int count)
    {
        foreach (var row in Rows(content, perRow).Take(count))
        {
            row.FileId = file.Id;
            row.Attempt = file.Attempt;
            await store.AddLineHashesAsync(row, TestContext.CancellationToken);
        }
    }

    /// <summary>
    /// Writes one import step as a batch.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="step">The step</param>
    /// <param name="records">The records of the batch</param>
    /// <returns>A task that completes when the batch is written</returns>
    private async Task WriteStepAsync(SqliteStore store, ImportStep step, params DataRecord[] records)
    {
        var batch = Samples.Batch(records);

        batch.Import = step;
        await store.WriteBatchAsync(batch, TestContext.CancellationToken);
    }

    /// <summary>
    /// Begins an import of a content, stores its line hashes of the attempt unless told not to, and completes it.
    /// </summary>
    /// <param name="store">The store</param>
    /// <param name="content">The content</param>
    /// <param name="name">The display path</param>
    /// <param name="type">The source type</param>
    /// <param name="withHashes">Whether the line hashes are stored; the completing step then carries the attempt, otherwise attempt 0</param>
    /// <param name="perRow">The number of hashes per row</param>
    /// <returns>A task that returns the state as it was created</returns>
    private async Task<ImportFile> ImportAsync(SqliteStore store, byte[] content, string name, string type = "syslog", bool withHashes = true, int perRow = StorageLimits.MaxLineHashes)
    {
        var file = await store.BeginImportAsync(ContentStart(content, name, type), TestContext.CancellationToken);

        if (withHashes)
        {
            await AddRowsAsync(store, file, content, perRow, int.MaxValue);
        }

        await WriteStepAsync(store, Step(file.Id, 0, withHashes ? file.Attempt : 0, true));

        return file;
    }

    /// <summary>
    /// Reads the first value of a query as text.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The query</param>
    /// <returns>A task that returns the value</returns>
    private async Task<string> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.CancellationToken), CultureInfo.InvariantCulture)!;
    }

    /// <summary>
    /// Runs a statement on the database file.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="sql">The statement</param>
    /// <returns>A task that completes when the statement ran</returns>
    private async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    /// <summary>
    /// Inserts a row of line hashes with SQL, bypassing the store's rules.
    /// </summary>
    /// <param name="path">The database file</param>
    /// <param name="row">The row</param>
    /// <returns>A task that completes when the row is inserted</returns>
    private async Task InsertRowAsync(string path, ImportLineHashes row)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        using var command = connection.CreateCommand();

        command.CommandText = "INSERT INTO import_line_hashes(file_id, attempt, first_offset, last_offset, hashes) VALUES ($id, $attempt, $first, $last, $hashes)";
        command.Parameters.AddWithValue("$id", row.FileId);
        command.Parameters.AddWithValue("$attempt", row.Attempt);
        command.Parameters.AddWithValue("$first", row.FirstOffset);
        command.Parameters.AddWithValue("$last", row.LastOffset);
        command.Parameters.AddWithValue("$hashes", row.Hashes);
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    #endregion // Methods
}