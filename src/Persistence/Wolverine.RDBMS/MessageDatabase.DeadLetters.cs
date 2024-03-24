﻿using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public async Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string tenantId)
    {
        var query = $"select {DatabaseConstants.DeadLetterFields} from {SchemaName}.{DatabaseConstants.DeadLetterTable} where 1 = 1";
        
        if (!string.IsNullOrEmpty(queryParameters.ExceptionType))
        {
            query += $" and {DatabaseConstants.ExceptionType} = @exceptionType";
        }

        if (!string.IsNullOrEmpty(queryParameters.MessageType))
        {
            query += $" and {DatabaseConstants.MessageType} = @messageType";
        }

        if (queryParameters.From.HasValue)
        {
            query += $" and {DatabaseConstants.ReceivedAt} >= @from";
        }

        if (queryParameters.Until.HasValue)
        {
            query += $" and {DatabaseConstants.ReceivedAt}  <= @until";
        }

        if (queryParameters.StartId.HasValue)
        {
            query += $" and {DatabaseConstants.Id} >= @startId";
        }

        var command = CreateCommand(query);

        if (!string.IsNullOrEmpty(queryParameters.ExceptionType))
        {
            command = command.With("exceptionType", queryParameters.ExceptionType);
        }

        if (!string.IsNullOrEmpty(queryParameters.MessageType))
        {
            command = command.With("messageType", queryParameters.MessageType);
        }

        if (queryParameters.From.HasValue)
        {
            command = command.With("from", queryParameters.From.Value);
        }

        if (queryParameters.Until.HasValue)
        {
            command = command.With("until", queryParameters.Until.Value);
        }

        if (queryParameters.StartId.HasValue)
        {
            command = command.With("startId", queryParameters.StartId.Value);
        }

        command.With("limit", queryParameters.Limit);

        var deadLetterEnvelopes = (List<DeadLetterEnvelope>)await command.FetchListAsync(reader =>
            DatabasePersistence.ReadDeadLetterAsync(reader, _cancellation), cancellation: _cancellation);

        var nextId = deadLetterEnvelopes.LastOrDefault()?.Envelope.Id;
        if (deadLetterEnvelopes.Count > 1)
        {
            deadLetterEnvelopes.RemoveAt(deadLetterEnvelopes.Count - 1);
        }

        return new(deadLetterEnvelopes, nextId.GetValueOrDefault(), tenantId);
    }

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
