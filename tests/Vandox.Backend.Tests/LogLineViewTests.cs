using System.Reflection;

using Vandox.Backend.Logs;
using Vandox.Core.Model;
using Vandox.Storage;

namespace Vandox.Backend.Tests;

/// <summary>
/// Tests for <see cref="LogLineView"/>
/// </summary>
[TestClass]
public class LogLineViewTests
{
    #region Methods

    /// <summary>
    /// The view caps the message at 16,384 characters and every other field at 2,048, and holds nothing of the record.
    /// </summary>
    [TestMethod]
    public void LogLineViewFromCapsEveryField()
    {
        // Arrange
        var record = new StoredRecord
                     {
                         Id = 77,
                         Record = new DataRecord
                                  {
                                      Origin = RecordOrigin.Backend,
                                      Source = new string('\u0000', 3000),
                                      CapturedAt = new DateTimeOffset(2026, 10, 12, 8, 9, 10, 123, TimeSpan.Zero),
                                      Data = new LogLine
                                             {
                                                 Log = "syslog",
                                                 Host = new string('h', 3000),
                                                 Program = new string((char)0x202e, 3000),
                                                 Pid = 1234,
                                                 Priority = 4,
                                                 Message = new string('\u0000', LogText.MaxChars)
                                             }
                                  }
                     };

        // Act
        var view = LogLineView.From(record);

        // Assert
        Assert.AreEqual(0, record.Record.Source[0], "the input holds the character, so the escape survived");
        Assert.AreEqual(77, view.Id, "record ID");
        Assert.AreEqual(record.Record.CapturedAt, view.CapturedAt, "capture time");
        Assert.AreEqual("warning", view.Severity, "severity name");
        Assert.AreEqual(1234, view.Pid, "process ID");
        Assert.IsLessThanOrEqualTo(LogText.MaxChars + 64, Chars(view.Message), "the message is capped at 16384 characters plus the marker");
        Assert.IsLessThanOrEqualTo(LogText.MaxShortChars + 64, Chars(view.Source), "the source is capped at 2048 characters plus the marker");
        Assert.IsLessThanOrEqualTo(LogText.MaxShortChars + 64, Chars(view.Host), "the host is capped at 2048 characters plus the marker");
        Assert.IsLessThanOrEqualTo(LogText.MaxShortChars + 64, Chars(view.Program), "the program is capped at 2048 characters plus the marker");
        Assert.Contains(run => run.Text.StartsWith("[cut: ", StringComparison.Ordinal), view.Message, "the message ends in a cut marker");
        Assert.Contains(run => run.Text.StartsWith("[cut: ", StringComparison.Ordinal), view.Source, "the source ends in a cut marker");
        Assert.Contains(run => run.Text.StartsWith("[cut: ", StringComparison.Ordinal), view.Host, "a host of plain characters is cut too");
        Assert.IsEmpty(HeldRecordTypes(), "the view holds no reference to the record or its payload");
    }

    /// <summary>
    /// A line without a priority has the severity dash.
    /// </summary>
    [TestMethod]
    public void LogLineViewFromNamesALineWithoutPriority()
    {
        // Arrange
        var record = new StoredRecord
                     {
                         Id = 1,
                         Record = new DataRecord
                                  {
                                      Origin = RecordOrigin.Backend,
                                      Source = "syslog",
                                      CapturedAt = new DateTimeOffset(2026, 10, 12, 8, 9, 10, TimeSpan.Zero),
                                      Data = new LogLine
                                             {
                                                 Log = "syslog",
                                                 Message = "plain"
                                             }
                                  }
                     };

        // Act
        var view = LogLineView.From(record);

        // Assert
        Assert.AreEqual("-", view.Severity, "no priority");
        Assert.AreEqual("plain", string.Concat(view.Message.Select(run => run.Text)), "the message");
    }

    /// <summary>
    /// Counts the characters of the runs.
    /// </summary>
    /// <param name="runs">The runs</param>
    /// <returns>The number of characters</returns>
    private static int Chars(IReadOnlyList<TextRun> runs)
    {
        return runs.Sum(run => run.Text.Length);
    }

    /// <summary>
    /// Finds the fields and properties of the view that can hold the record or a part of it.
    /// </summary>
    /// <returns>The names of the members</returns>
    private static string[] HeldRecordTypes()
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var types = new[] { typeof(StoredRecord), typeof(DataRecord), typeof(IPayload), typeof(LogLine) };

        return typeof(LogLineView).GetFields(flags)
                                  .Where(field => types.Contains(field.FieldType))
                                  .Select(field => field.Name)
                                  .Concat(typeof(LogLineView).GetProperties(flags).Where(property => types.Contains(property.PropertyType)).Select(property => property.Name))
                                  .ToArray();
    }

    #endregion // Methods
}