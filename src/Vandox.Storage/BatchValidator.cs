using Vandox.Core.Wire;

namespace Vandox.Storage;

/// <summary>
/// Checks a batch completely before anything is written.
/// </summary>
internal static class BatchValidator
{
    #region Methods

    /// <summary>
    /// Checks a batch.
    /// </summary>
    /// <param name="batch">The batch</param>
    /// <exception cref="InvalidBatchException">The batch breaks a rule; the message names the rule, never a payload value</exception>
    internal static void Validate(RecordBatch batch)
    {
        if (batch.Records.Count == 0 && batch.Import is not { Complete: true })
        {
            throw Invalid("no records");
        }

        if (batch.Records.Count > StorageLimits.MaxBatchRecords)
        {
            throw Invalid($"more than {StorageLimits.MaxBatchRecords} records");
        }

        ValidateReceivedAt(batch);

        if (batch.AgentId.Length > 0)
        {
            var error = WireFormat.ValidateAgentId(batch.AgentId);

            if (error is not null)
            {
                throw Invalid(error.Message);
            }
        }

        if (batch.Import is null)
        {
            ValidateRecords(batch);
        }
        else
        {
            ValidateImport(batch);
        }
    }

    /// <summary>
    /// Creates the error of a rejected batch.
    /// </summary>
    /// <param name="rule">The rule that is broken</param>
    /// <returns>The error</returns>
    private static InvalidBatchException Invalid(string rule)
    {
        return new InvalidBatchException($"store: invalid batch: {rule}");
    }

    /// <summary>
    /// Checks the receive time of a batch.
    /// </summary>
    /// <param name="batch">The batch</param>
    private static void ValidateReceivedAt(RecordBatch batch)
    {
        if (batch.ReceivedAt == default)
        {
            throw Invalid("received_at is required");
        }

        if (batch.ReceivedAt.Offset != TimeSpan.Zero)
        {
            throw Invalid("received_at must be UTC");
        }

        if (StorageTime.IsOutsideStorableRange(batch.ReceivedAt))
        {
            throw Invalid("received_at is outside the storable range");
        }
    }

    /// <summary>
    /// Checks the records of a batch without an import step.
    /// </summary>
    /// <param name="batch">The batch</param>
    private static void ValidateRecords(RecordBatch batch)
    {
        for (var index = 0; index < batch.Records.Count; index++)
        {
            var error = RecordRules.Check(batch.Records[index], batch.AgentId);

            if (error is not null)
            {
                throw Invalid($"records[{index}]: {error}");
            }
        }
    }

    /// <summary>
    /// Checks the rules of a batch with an import step: no agent, a valid step, import records only.
    /// </summary>
    /// <param name="batch">The batch</param>
    private static void ValidateImport(RecordBatch batch)
    {
        if (batch.AgentId.Length > 0)
        {
            throw Invalid("an import batch has no agent ID");
        }

        if (batch.Import!.FileId <= 0)
        {
            throw Invalid("import file ID must be positive");
        }

        if (batch.Import.Done < 0)
        {
            throw Invalid("import done must not be negative");
        }

        for (var index = 0; index < batch.Records.Count; index++)
        {
            var error = RecordRules.CheckImportRecord(batch.Records[index]);

            if (error is not null)
            {
                throw Invalid($"records[{index}]: {error}");
            }
        }
    }

    #endregion // Methods
}