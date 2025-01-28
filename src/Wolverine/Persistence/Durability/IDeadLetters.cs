namespace Wolverine.Persistence.Durability;

/// <summary>
/// Summarized count of dead letter messages
/// </summary>
/// <param name="ServiceName"></param>
/// <param name="ReceivedAt"></param>
/// <param name="MessageType"></param>
/// <param name="ExceptionType"></param>
/// <param name="TenantId"></param>
/// <param name="Count"></param>
public record DeadLetterQueueCount(string ServiceName, Uri ReceivedAt, string MessageType, string ExceptionType, string TenantId, int Count);


public interface IDeadLetters
{
    Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId = null);

    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null);

    /// <summary>
    ///     Marks the Envelopes in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="exceptionType">Exception Type that should be marked. Default is any.</param>
    /// <returns>Number of envelopes marked.</returns>
    Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "");

    /// <summary>
    ///     Marks the Envelope in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null);

    /// <summary>
    /// Deletes the DeadLetterEnvelope from the DeadLetterTable
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null);
}