using Microsoft.Data.Sqlite;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="LineIndex"/>
/// </summary>
[TestClass]
public class LineIndexTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A match returns only the lines of its own call: the rows of an earlier call are gone, and an ID can be used again.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LineIndexMatchReturnsOnlyTheLinesOfTheCall()
    {
        // Arrange
        using var index = LineIndex.Open(TestContext.CancellationToken);

        // Act
        var phrase = await index.MatchAsync([(1, "ww ww"), (2, "ww x ww")], "\"ww ww\"", TestContext.CancellationToken);
        var word = await index.MatchAsync([(3, "ww x")], "\"ww\"", TestContext.CancellationToken);
        var again = await index.MatchAsync([(1, "other")], "\"ww\"", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual([1L], phrase.Order().ToArray(), "the phrase is in line 1 only");
        Assert.AreSequenceEqual([3L], word.Order().ToArray(), "the lines of the first call are not matched again");
        Assert.IsEmpty(again, "an ID of an earlier call can be used with another message");
    }

    /// <summary>
    /// The index tokenizes like the stored index: the stored index has no option other than the content table, the column and the tokenizer of the line index, and case, diacritics and separators behave alike.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LineIndexUsesTheTokenizerOfTheIndex()
    {
        // Arrange
        using var directory = new TempDirectory();

        var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken);

        await store.DisposeAsync();

        var sql = await StoredIndexSqlAsync(directory.Path);
        var options = Options(sql).Where(IsOptionOfTheIndex).ToArray();
        var accent = char.ConvertFromUtf32(0xE9);
        using var index = LineIndex.Open(TestContext.CancellationToken);

        // Act
        var matched = await index.MatchAsync([(1, $"Caf{accent}-Bar open"), (2, "cafe bar"), (3, "cafebar"), (4, "ww.ww"), (5, "ww ww"), (6, "ww_ww")], "\"CAFE bar\"", TestContext.CancellationToken);
        var dotted = await index.MatchAsync([(4, "ww.ww"), (5, "ww ww"), (6, "ww_ww"), (7, "ww x ww")], "\"ww.ww\"", TestContext.CancellationToken);

        // Assert
        Assert.AreSequenceEqual(["message", $"tokenize='{LineIndex.Tokenizer}'"], options, "every option of the stored index but the content table: the column, no detail, no prefix, the tokenizer");
        Assert.AreSequenceEqual([1L, 2L], matched.Order().ToArray(), "case and diacritics are ignored, a hyphen separates");
        Assert.AreSequenceEqual([4L, 5L, 6L], dotted.Order().ToArray(), "dots and underscores separate parts");
    }

    /// <summary>
    /// A match with a cancelled token ends with a cancellation and not with a database error, and the index stays usable.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LineIndexMatchWithACancelledTokenThrows()
    {
        // Arrange
        using var index = LineIndex.Open(TestContext.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => index.MatchAsync([(1, "ww ww")], "\"ww\"", new CancellationToken(true)));
        var later = await index.MatchAsync([(2, "x")], "\"ww\"", TestContext.CancellationToken);

        // Assert
        Assert.IsNotNull(exception, "the cancellation ends the match");
        Assert.IsEmpty(later, "no row of the cancelled call is left in the index");
    }

    /// <summary>
    /// An index opened with a cancelled token fails with a cancellation, at the open or at the first match, never with a database error, and can be disposed twice.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task LineIndexOpenedWithACancelledTokenFailsWithACancellation()
    {
        // Arrange
        var cancelled = new CancellationToken(true);
        LineIndex? index = null;

        // Act
        var atOpen = Record(() => index = LineIndex.Open(cancelled));
        Exception? atMatch = null;

        if (index is not null)
        {
            try
            {
                await index.MatchAsync([(1, "ww")], "\"ww\"", cancelled);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SqliteException or StoreException)
            {
                atMatch = exception;
            }

            index.Dispose();
            index.Dispose();
        }

        // Assert
        var failure = atOpen ?? atMatch;

        Assert.IsNotNull(failure, "the cancellation is seen at the open or at the first match");
        Assert.IsInstanceOfType<OperationCanceledException>(failure, "it is a cancellation, not a database error");
    }

    /// <summary>
    /// Splits the arguments of a <c>CREATE VIRTUAL TABLE ... USING fts5(...)</c> statement at the commas outside quotes and removes white space around the equal signs.
    /// </summary>
    /// <param name="sql">The statement</param>
    /// <returns>The arguments, trimmed, with white space collapsed</returns>
    private static string[] Options(string sql)
    {
        var start = sql.IndexOf("fts5(", StringComparison.OrdinalIgnoreCase) + "fts5(".Length;
        var end = sql.LastIndexOf(')');
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quotes = 0;

        foreach (var character in sql[start..end])
        {
            if (character == '\'')
            {
                quotes++;
            }

            if (character == ',' && quotes % 2 == 0)
            {
                parts.Add(current.ToString());
                current.Clear();

                continue;
            }

            current.Append(character);
        }

        parts.Add(current.ToString());

        return parts.Select(part => string.Join(' ', part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace(" = ", "=", StringComparison.Ordinal).Replace("= ", "=", StringComparison.Ordinal).Replace(" =", "=", StringComparison.Ordinal)).ToArray();
    }

    /// <summary>
    /// Runs an action and returns the cancellation or database error it throws.
    /// </summary>
    /// <param name="action">The action</param>
    /// <returns>The exception, or <c>null</c></returns>
    private static Exception? Record(Action action)
    {
        try
        {
            action();

            return null;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SqliteException or StoreException)
        {
            return exception;
        }
    }

    /// <summary>
    /// Tells whether an option of the stored index is one the line index must share: everything but the content table.
    /// </summary>
    /// <param name="option">The option</param>
    /// <returns><c>true</c> when the option is not about the content table</returns>
    private static bool IsOptionOfTheIndex(string option)
    {
        if (option.StartsWith("content=", StringComparison.OrdinalIgnoreCase) || option.StartsWith("content_rowid=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the text of the statement that created the stored index.
    /// </summary>
    /// <param name="directory">The database directory</param>
    /// <returns>A task that returns the statement</returns>
    private async Task<string> StoredIndexSqlAsync(string directory)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, StorageLimits.FileName)};Mode=ReadOnly;Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'log_fts'";

        return (string)(await command.ExecuteScalarAsync(TestContext.CancellationToken))!;
    }

    #endregion // Methods
}