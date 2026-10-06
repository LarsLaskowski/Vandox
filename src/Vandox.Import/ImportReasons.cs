using System.Formats.Tar;
using System.IO.Compression;

using Vandox.Core.IO;
using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// Turns exceptions into the short texts of the summary, without the path of an operating system error.
/// </summary>
internal static class ImportReasons
{
    #region Methods

    /// <summary>
    /// Returns the text of an exception for the summary.
    /// </summary>
    /// <param name="exception">The exception</param>
    /// <returns>The text; never a path</returns>
    internal static string Of(Exception exception)
    {
        return exception switch
               {
                   SafeIoException safe => safe.Message,
                   FileNotFoundException or DirectoryNotFoundException => "no such file or directory",
                   UnauthorizedAccessException => "permission denied",
                   InvalidDataException => "invalid compressed data",
                   EndOfStreamException => "unexpected end of data",
                   IOException => "i/o error",
                   StoreException => "database error",
                   _ => exception.Message
               };
    }

    #endregion // Methods
}