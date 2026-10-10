using Vandox.Storage;

namespace Vandox.Import;

/// <summary>
/// The part of the database the importer writes to.
/// </summary>
public interface IImportStore : IRecordWriter, IImportTracker;