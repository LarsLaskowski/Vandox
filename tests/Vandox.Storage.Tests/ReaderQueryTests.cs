using Microsoft.Data.Sqlite;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="ReaderQuery"/>
/// </summary>
[TestClass]
public class ReaderQueryTests
{
    #region Constants

    /// <summary>
    /// The SQLite result code of an interrupted statement.
    /// </summary>
    private const int SqliteInterrupt = 9;

    /// <summary>
    /// The row count of the statements that a test cancels.
    /// </summary>
    private const int LongRows = 20000000;

    /// <summary>
    /// The row count of the statements that run to their end.
    /// </summary>
    private const int ShortRows = 1500000;

    /// <summary>
    /// The context of the translated errors.
    /// </summary>
    private const string Context = "store: reading records";

    #endregion // Constants

    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// A cancelled token stops a statement inside its first step with SQLITE_INTERRUPT.
    /// </summary>
    [TestMethod]
    public void ReaderQueryAttachInterruptsFirstStepWhenCancelled()
    {
        // Arrange
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var connection = OpenConnection(source);

        ReaderQuery.Attach(connection, source.Token);

        using var command = connection.CreateCommand();

        command.CommandText = CountSql(LongRows, 1);

        // Act
        var exception = Assert.ThrowsExactly<SqliteException>(() => command.ExecuteScalar(), "the statement is interrupted");

        // Assert
        Assert.AreEqual(SqliteInterrupt, exception.SqliteErrorCode, "SQLITE_INTERRUPT");
    }

    /// <summary>
    /// A statement interrupted in its first step ends in an OperationCanceledException with the token, the SQLite error and the context.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReaderQueryReadThrowsCanceledWhenInterruptedInFirstStep()
    {
        // Arrange
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var connection = OpenConnection(source);

        ReaderQuery.Attach(connection, source.Token);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ReaderQuery.ReadAsync(connection, CountSql(LongRows, 1), new Dictionary<string, object>(), reader => reader.GetInt64(0), Context, source.Token), "the interrupted statement is a cancellation");

        // Assert
        AssertCanceled(exception, source.Token);
    }

    /// <summary>
    /// A statement interrupted after its first row ends in an OperationCanceledException with the token, the SQLite error and the context.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReaderQueryReadThrowsCanceledWhenInterruptedInLaterStep()
    {
        // Arrange
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var connection = OpenConnection(source);
        var read = new List<long>();

        ReaderQuery.Attach(connection, source.Token);

        // Act
        var exception = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ReaderQuery.ReadAsync(connection,
                                                                                                                RowsSql(LongRows, 2),
                                                                                                                new Dictionary<string, object>(),
                                                                                                                reader =>
                                                                                                                {
                                                                                                                    read.Add(reader.GetInt64(0));

                                                                                                                    return reader.GetInt64(0);
                                                                                                                },
                                                                                                                Context,
                                                                                                                source.Token),
                                                                                    "the interrupted statement is a cancellation");

        // Assert
        AssertCanceled(exception, source.Token);
        Assert.AreSequenceEqual([1L], read, "row 1 was read before the interruption");
    }

    /// <summary>
    /// Without a handler and with a handler of a token that is never cancelled, the statement runs to its end.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task ReaderQueryReadCompletesWhenNotCancelled()
    {
        // Arrange
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var connection = OpenConnection(source);
        var none = new Dictionary<string, object>();

        // Act
        var uncancellable = await ReaderQuery.ReadAsync(connection, CountSql(ShortRows, -1), none, reader => reader.GetInt64(0), Context, CancellationToken.None);

        ReaderQuery.Attach(connection, source.Token);

        var counted = await ReaderQuery.ReadAsync(connection, CountSql(ShortRows, -1), none, reader => reader.GetInt64(0), Context, source.Token);
        var rows = await ReaderQuery.ReadAsync(connection, RowsSql(ShortRows, -1), none, reader => reader.GetInt64(0), Context, source.Token);

        // Assert
        Assert.AreSequenceEqual([(long)ShortRows], uncancellable, "no handler for a token that cannot be cancelled");
        Assert.AreSequenceEqual([(long)ShortRows], counted, "a token that is never cancelled interrupts nothing");
        Assert.AreSequenceEqual([1L, ShortRows], rows, "the rows of a later-step statement");
    }

    /// <summary>
    /// An interrupt with a cancelled token is translated to an OperationCanceledException.
    /// </summary>
    [TestMethod]
    public void ReaderQueryTranslateMapsInterruptToCanceled()
    {
        // Arrange
        using var source = new CancellationTokenSource();
        var inner = new SqliteException("interrupted", SqliteInterrupt);

        source.Cancel();

        // Act
        var translated = ReaderQuery.Translate(inner, Context, source.Token);

        // Assert
        var canceled = Assert.IsInstanceOfType<OperationCanceledException>(translated, "an interrupt of a cancelled call");

        Assert.AreEqual($"{Context}: cancelled", canceled.Message, "message");
        Assert.AreEqual(source.Token, canceled.CancellationToken, "token");
        Assert.AreSame(inner, canceled.InnerException, "inner");
    }

    /// <summary>
    /// Every other SQLite error is translated to a StoreException with the message of the operation.
    /// </summary>
    [TestMethod]
    public void ReaderQueryTranslateReturnsStoreExceptionOtherwise()
    {
        // Arrange
        using var cancelled = new CancellationTokenSource();
        using var uncancelled = new CancellationTokenSource();

        cancelled.Cancel();

        var cases = new (int Code, CancellationToken Token, string Name)[]
                    {
                        (1, cancelled.Token, "another error with a cancelled token"),
                        (SqliteInterrupt, uncancelled.Token, "an interrupt with an uncancelled token"),
                        (5, CancellationToken.None, "another error with no token")
                    };

        foreach (var (code, token, name) in cases)
        {
            var inner = new SqliteException("database is locked", code);

            // Act
            var translated = ReaderQuery.Translate(inner, Context, token);

            // Assert
            var store = Assert.IsInstanceOfType<StoreException>(translated, name);

            Assert.AreEqual($"{Context}: {inner.Message}", store.Message, $"message of {name}");
            Assert.Contains("database is locked", store.Message, $"the text of the SQLite error is kept for {name}");
            Assert.AreSame(inner, store.InnerException, $"inner of {name}");
        }
    }

    /// <summary>
    /// Checks the exception of an interrupted read.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <param name="token">The token of the call</param>
    private static void AssertCanceled(OperationCanceledException exception, CancellationToken token)
    {
        var inner = Assert.IsInstanceOfType<SqliteException>(exception.InnerException, "inner exception");

        Assert.AreEqual(SqliteInterrupt, inner.SqliteErrorCode, "SQLITE_INTERRUPT");
        Assert.AreEqual(token, exception.CancellationToken, "token of the call");
        Assert.AreEqual("store: reading records: cancelled", exception.Message, "message");
    }

    /// <summary>
    /// Opens an in-memory connection with the function <c>cancel_at</c>, which cancels the source when its first argument equals the second.
    /// </summary>
    /// <param name="source">The source to cancel</param>
    /// <returns>The open connection</returns>
    private static SqliteConnection OpenConnection(CancellationTokenSource source)
    {
        var connection = new SqliteConnection("Data Source=:memory:");

        connection.Open();
        connection.CreateFunction("cancel_at",
                                  (long value, long at) =>
                                  {
                                      if (value == at)
                                      {
                                          source.Cancel();
                                      }

                                      return 0L;
                                  });

        return connection;
    }

    /// <summary>
    /// Gets the statement that counts a recursive series inside one step.
    /// </summary>
    /// <param name="rows">The length of the series</param>
    /// <param name="at">The value at which the statement cancels, or -1 for never</param>
    /// <returns>The statement</returns>
    private static string CountSql(int rows, int at)
    {
        return $"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {rows}) SELECT count(*) FROM c WHERE cancel_at(x, {at}) = 0";
    }

    /// <summary>
    /// Gets the statement that returns row 1 and the last row of a recursive series and cancels while it looks for the second.
    /// </summary>
    /// <param name="rows">The length of the series</param>
    /// <param name="at">The value at which the statement cancels, or -1 for never</param>
    /// <returns>The statement</returns>
    private static string RowsSql(int rows, int at)
    {
        return $"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {rows}) SELECT x FROM c WHERE x = 1 OR x = {rows} OR cancel_at(x, {at}) = 1";
    }

    #endregion // Methods
}