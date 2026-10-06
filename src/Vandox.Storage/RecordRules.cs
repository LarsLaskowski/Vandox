using Vandox.Core.Model;

namespace Vandox.Storage;

/// <summary>
/// The rules a record must satisfy to be stored, on top of the model rules: the storable time range and the
/// sequence number range.
/// </summary>
public static class RecordRules
{
    #region Methods

    /// <summary>
    /// Checks a record with the model rules and the limits of the database.
    /// </summary>
    /// <param name="record">The record</param>
    /// <param name="agentId">The agent ID of the batch; empty when it names none</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    public static string? Check(DataRecord record, string agentId)
    {
        var error = record.Validate();

        if (error is not null)
        {
            return error.Message;
        }

        if (StorageTime.IsOutsideStorableRange(record.CapturedAt))
        {
            return "captured_at is outside the storable range";
        }

        if (record.Seq > long.MaxValue)
        {
            return "seq is above the storable range";
        }

        if (record.Origin == RecordOrigin.Agent && agentId.Length == 0)
        {
            return "origin agent needs an agent ID in the batch";
        }

        return null;
    }

    /// <summary>
    /// Checks a record for a batch with an import step: the model rules, origin import and the storable time range.
    /// </summary>
    /// <param name="record">The record</param>
    /// <returns>The broken rule, or <c>null</c></returns>
    public static string? CheckImportRecord(DataRecord record)
    {
        var error = Check(record, string.Empty);

        if (error is not null)
        {
            return error;
        }

        return record.Origin == RecordOrigin.Import ? null : "origin must be import";
    }

    #endregion // Methods
}