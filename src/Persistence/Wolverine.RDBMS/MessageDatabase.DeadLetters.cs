using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> 
{
    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        await using var reader = await CreateCommand(
                $"select {DatabaseConstants.DeadLetterFields} from {SchemaName}.{DatabaseConstants.DeadLetterTable} where id = @id")
            .With("id", id)
            .ExecuteReaderAsync(_cancellation);

        if (!await reader.ReadAsync(_cancellation))
        {
            await reader.CloseAsync();
            return null;
        }

        var deadLetterEnvelope = await DatabasePersistence.ReadDeadLetterAsync(reader, _cancellation);
        await reader.CloseAsync();

        return deadLetterEnvelope;
    }


}
