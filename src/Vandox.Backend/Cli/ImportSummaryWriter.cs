using System.Globalization;
using System.Text;

using Vandox.Import;

namespace Vandox.Backend.Cli;

/// <summary>
/// Writes the summary of an import as text: every path and reason is quoted.
/// </summary>
internal static class ImportSummaryWriter
{
    #region Methods

    /// <summary>
    /// Writes a summary.
    /// </summary>
    /// <param name="writer">The destination</param>
    /// <param name="summary">The summary</param>
    /// <returns>A task that completes when the summary is written</returns>
    internal static Task WriteAsync(TextWriter writer, ImportSummary summary)
    {
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"Import of {Terminal.Quote(summary.Root)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Files found:         {summary.Files.Count}\n");
        text.Append(CultureInfo.InvariantCulture, $"  imported:          {summary.Count(ImportOutcome.Imported)}\n");
        text.Append(CultureInfo.InvariantCulture, $"  already imported:  {summary.Count(ImportOutcome.AlreadyImported)}\n");
        text.Append(CultureInfo.InvariantCulture, $"  not recognized:    {summary.Count(ImportOutcome.Unrecognized)}\n");
        text.Append(CultureInfo.InvariantCulture, $"  failed:            {summary.Count(ImportOutcome.Failed)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Lines read:          {summary.Lines}\n");
        text.Append(CultureInfo.InvariantCulture, $"Records stored:      {summary.Records}\n");
        text.Append(CultureInfo.InvariantCulture, $"Lines skipped:       {summary.Skipped}\n");

        if (summary.First is { } first && summary.Last is { } last)
        {
            text.Append(CultureInfo.InvariantCulture, $"Time range:          {Format(first)} to {Format(last)}\n");
        }
        else
        {
            text.Append("Time range:          no records were stored\n");
        }

        if (summary.Interrupted)
        {
            text.Append("The import was interrupted; run it again to continue.\n");
        }

        WriteListed(text, "Files not recognized", summary.Files, ImportOutcome.Unrecognized);
        WriteListed(text, "Files that failed", summary.Files, ImportOutcome.Failed);
        WriteSkipped(text, summary.Files);

        return writer.WriteAsync(text.ToString());
    }

    /// <summary>
    /// Formats a time as RFC 3339 in UTC.
    /// </summary>
    /// <param name="time">The time</param>
    /// <returns>The text</returns>
    private static string Format(DateTimeOffset time)
    {
        return time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Lists the files with an outcome and their reasons under a heading.
    /// </summary>
    /// <param name="text">The text so far</param>
    /// <param name="heading">The heading</param>
    /// <param name="files">The files</param>
    /// <param name="outcome">The outcome to list</param>
    private static void WriteListed(StringBuilder text, string heading, List<FileResult> files, ImportOutcome outcome)
    {
        var listed = files.Where(file => file.Outcome == outcome).ToList();

        if (listed.Count == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n{heading}:\n");

        foreach (var file in listed)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {Terminal.Quote(file.Path)}: {Terminal.Quote(file.Reason)}\n");
        }
    }

    /// <summary>
    /// Lists the files with lines a parser skipped, with the first reasons.
    /// </summary>
    /// <param name="text">The text so far</param>
    /// <param name="files">The files</param>
    private static void WriteSkipped(StringBuilder text, List<FileResult> files)
    {
        var skipped = files.Where(file => file.Skipped > 0).ToList();

        if (skipped.Count == 0)
        {
            return;
        }

        text.Append("\nLines skipped by the parsers:\n");

        foreach (var file in skipped)
        {
            text.Append(CultureInfo.InvariantCulture, $"  {Terminal.Quote(file.Path)}: {file.Skipped} lines\n");

            foreach (var problem in file.Problems)
            {
                text.Append(problem.Line > 0 ? $"    line {problem.Line.ToString(CultureInfo.InvariantCulture)}: {Terminal.Quote(problem.Reason)}\n" : $"    {Terminal.Quote(problem.Reason)}\n");
            }
        }
    }

    #endregion // Methods
}