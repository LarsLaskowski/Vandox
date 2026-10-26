using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

namespace Vandox.Storage.Tests;

/// <summary>
/// Tests for <see cref="AgentTimelineQueries"/>
/// </summary>
[TestClass]
public class AgentTimelineQueriesTests
{
    #region Properties

    /// <summary>
    /// Gets or sets the context of the running test.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    #endregion // Properties

    #region Methods

    /// <summary>
    /// Every statement the timeline runs on the gap, stream, clock sample and sample time tables is an index search, never a scan of a table.
    /// </summary>
    /// <returns>A task that completes when the test is done</returns>
    [TestMethod]
    public async Task AgentTimelineQueriesUseIndexSearchesOnly()
    {
        // Arrange
        using var directory = new TempDirectory();

        await using (var store = await SqliteStore.OpenAsync(directory.Path, TestContext.CancellationToken))
        {
            await store.PingAsync(TestContext.CancellationToken);
        }

        var statements = typeof(AgentTimelineQueries).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                                     .Where(field => field.FieldType == typeof(string))
                                                     .Select(field => (field.Name, Sql: (string)(field.IsLiteral ? field.GetRawConstantValue()! : field.GetValue(null)!)))
                                                     .Where(statement => Regex.IsMatch(statement.Sql, @"\b(gaps|agent_streams|agent_clock_samples|agent_sample_times)\b"))
                                                     .ToList();

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory.Path, StorageLimits.FileName)};Pooling=False");

        await connection.OpenAsync(TestContext.CancellationToken);

        // Act
        var plans = new List<(string Name, string Detail)>();

        foreach (var (name, sql) in statements)
        {
            using var command = connection.CreateCommand();

            command.CommandText = $"EXPLAIN QUERY PLAN {sql}";

            foreach (Match parameter in Regex.Matches(sql, @"[$@:][A-Za-z_][A-Za-z0-9_]*").DistinctBy(match => match.Value))
            {
                command.Parameters.AddWithValue(parameter.Value, 1L);
            }

            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

            while (await reader.ReadAsync(TestContext.CancellationToken))
            {
                plans.Add((name, reader.GetString(3)));
            }
        }

        // Assert
        Assert.IsNotEmpty(statements, "the queries class holds the statements as string constants");
        Assert.IsGreaterThan(0, plans.Count(plan => Regex.IsMatch(plan.Detail, @"SEARCH gaps\b")), "the gap statements search the gap table");
        Assert.IsGreaterThan(0, plans.Count(plan => Regex.IsMatch(plan.Detail, @"SEARCH agent_sample_times\b")), "the sample time statements search their table");

        foreach (var (name, detail) in plans)
        {
            Assert.IsFalse(Regex.IsMatch(detail, @"SCAN (gaps|agent_streams|agent_clock_samples|agent_sample_times)\b"), $"{name} scans a table: {detail}");
        }
    }

    #endregion // Methods
}