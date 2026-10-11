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

    #endregion // Methods
}