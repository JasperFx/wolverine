using JasperFx.Core;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Oracle.Util;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    // IDeadLetters

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        await using var cmd = conn.CreateCommand(
            $"SELECT {DatabaseConstants.DeadLetterFields} FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE id = :id");
        cmd.With("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(_cancellation);

        if (!await reader.ReadAsync(_cancellation))
        {
            await reader.CloseAsync();
            await conn.CloseAsync();
            return null;
        }

        var deadLetterEnvelope = await OracleEnvelopeReader.ReadDeadLetterAsync(reader, _cancellation, Logger);
        await reader.CloseAsync();
        await conn.CloseAsync();

        return deadLetterEnvelope;
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();
        builder.Append(
            $"SELECT {DatabaseConstants.ReceivedAt}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExceptionType}, COUNT(*) as total");
        builder.Append($" FROM {SchemaName}.{DatabaseConstants.DeadLetterTable}");
        builder.Append(" WHERE 1 = 1");

        if (range.From.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.SentAt} >= ");
            builder.AppendParameter(range.From.Value.ToUniversalTime());
        }

        if (range.To.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.SentAt} <= ");
            builder.AppendParameter(range.To.Value.ToUniversalTime());
        }

        builder.Append(
            $" GROUP BY {DatabaseConstants.ReceivedAt}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExceptionType}");

        await using var cmd = builder.Compile();

        var envelopes = new List<DeadLetterQueueCount>();

        await using var conn = await _dataSource.OpenConnectionAsync(_cancellation);
        try
        {
            cmd.Connection = conn;
            await using var reader = await cmd.ExecuteReaderAsync(_cancellation);

            while (await reader.ReadAsync(token))
            {
                var uri = new Uri(await reader.GetFieldValueAsync<string>(0, token));
                var messageType = await reader.GetFieldValueAsync<string>(1, token);
                var exceptionType = await reader.GetFieldValueAsync<string>(2, token);
                var count = Convert.ToInt32(await reader.GetFieldValueAsync<decimal>(3, token));

                envelopes.Add(new DeadLetterQueueCount(serviceName, uri, messageType, exceptionType, Uri, count));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }

        return envelopes;
    }

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            return await queryWithHeaderFilterAsync(query, token);
        }

        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"SELECT {DatabaseConstants.DeadLetterFields}, COUNT(*) OVER() as total_rows FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");

        writeDeadLetterWhereClause(query, builder);

        builder.Append($" ORDER BY {DatabaseConstants.ExecutionTime}");

        if (query.PageNumber <= 0) query.PageNumber = 1;

        if (query.PageSize > 0)
        {
            var offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * query.PageSize;
            builder.Append($" OFFSET {offset} ROWS FETCH NEXT {query.PageSize} ROWS ONLY");
        }

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var results = new DeadLetterEnvelopeResults { PageNumber = query.PageNumber };
        if (await reader.ReadAsync(token))
        {
            var env = await OracleEnvelopeReader.ReadDeadLetterAsync(reader, token, Logger);
            results.Envelopes.Add(env);
            results.TotalCount = Convert.ToInt32(await reader.GetFieldValueAsync<decimal>(10, token));
        }

        while (await reader.ReadAsync(token))
        {
            var env = await OracleEnvelopeReader.ReadDeadLetterAsync(reader, token, Logger);
            results.Envelopes.Add(env);
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return results;
    }

    // GH-5164: headers only exist inside the serialized envelope body, so the header criteria are
    // applied in memory to every row matching the rest of the query. Paging and the total count are
    // computed over the header matches.
    private async Task<DeadLetterEnvelopeResults> queryWithHeaderFilterAsync(DeadLetterEnvelopeQuery query,
        CancellationToken token)
    {
        if (query.PageNumber <= 0) query.PageNumber = 1;
        var offset = query.PageSize > 0 && query.PageNumber > 1 ? (query.PageNumber - 1) * query.PageSize : 0;

        var results = new DeadLetterEnvelopeResults { PageNumber = query.PageNumber };
        var matched = 0;

        await foreach (var envelope in readHeaderMatchesAsync(query, token))
        {
            if (matched >= offset && (query.PageSize <= 0 || results.Envelopes.Count < query.PageSize))
            {
                results.Envelopes.Add(envelope);
            }

            matched++;
        }

        results.TotalCount = matched;
        return results;
    }

    private async IAsyncEnumerable<DeadLetterEnvelope> readHeaderMatchesAsync(DeadLetterEnvelopeQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();
        builder.Append(
            $"SELECT {DatabaseConstants.DeadLetterFields} FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");
        writeDeadLetterWhereClause(query, builder);
        // id breaks ties so that separate page requests see one stable order
        builder.Append($" ORDER BY {DatabaseConstants.ExecutionTime}, {DatabaseConstants.Id}");

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var envelope = await OracleEnvelopeReader.ReadDeadLetterAsync(reader, token, Logger);
            if (query.MatchesHeaders(envelope.Envelope))
            {
                yield return envelope;
            }
        }

        await reader.CloseAsync();
        await conn.CloseAsync();
    }

    // An empty result means "nothing matched" -- callers must not fall through to an unfiltered statement
    private async Task<List<Guid>> findHeaderMatchIdsAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var ids = new List<Guid>();
        await foreach (var envelope in readHeaderMatchesAsync(query, token))
        {
            ids.Add(envelope.Id);
        }

        return ids;
    }

    // Oracle caps an IN list at 1000 expressions
    private const int HeaderFilterIdChunkSize = 500;

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            var ids = await findHeaderMatchIdsAsync(query, token);
            foreach (var chunk in ids.Chunk(HeaderFilterIdChunkSize))
            {
                await discardAsync(new DeadLetterEnvelopeQuery { MessageIds = chunk }, token);
            }

            return;
        }

        await discardAsync(query, token);
    }

    private async Task discardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();

        builder.Append($"DELETE FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");

        writeDeadLetterWhereClause(query, builder);

        await using var cmd = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            var ids = await findHeaderMatchIdsAsync(query, token);
            foreach (var chunk in ids.Chunk(HeaderFilterIdChunkSize))
            {
                await replayAsync(new DeadLetterEnvelopeQuery { MessageIds = chunk }, token);
            }

            return;
        }

        await replayAsync(query, token);
    }

    private async Task replayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"UPDATE {SchemaName}.{DatabaseConstants.DeadLetterTable} SET {DatabaseConstants.Replayable} = ");
        builder.AppendParameter(1); // Oracle uses NUMBER(1) for bool
        builder.Append(" WHERE 1 = 1");
        writeDeadLetterWhereClause(query, builder);

        await using var cmd = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        var deadLetter = await DeadLetterEnvelopeByIdAsync(envelopeId);
        if (deadLetter == null) return;

        deadLetter.Envelope.Data = newBody;
        var serialized = EnvelopeSerializer.Serialize(deadLetter.Envelope);

        var builder = ToOracleCommandBuilder();
        builder.Append(
            $"UPDATE {SchemaName}.{DatabaseConstants.DeadLetterTable} SET {DatabaseConstants.Body} = ");
        builder.AppendParameter(serialized);
        builder.Append($", {DatabaseConstants.Replayable} = ");
        builder.AppendParameter(1); // Oracle uses NUMBER(1) for bool
        builder.Append($" WHERE {DatabaseConstants.Id} = ");
        builder.AppendParameter(envelopeId);

        await using var cmd = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private void writeDeadLetterWhereClause(DeadLetterEnvelopeQuery query, Weasel.Oracle.CommandBuilder builder)
    {
        if (query.Range.From.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.SentAt} >= ");
            builder.AppendParameter(query.Range.From.Value.ToUniversalTime());
        }

        if (query.Range.To.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.SentAt} <= ");
            builder.AppendParameter(query.Range.To.Value.ToUniversalTime());
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            builder.Append($" AND {DatabaseConstants.ExceptionType} = ");
            builder.AppendParameter(query.ExceptionType);
        }

        if (query.ExceptionMessage.IsNotEmpty())
        {
            builder.Append($" AND {DatabaseConstants.ExceptionMessage} LIKE ");
            builder.AppendParameter(query.ExceptionMessage);
        }

        if (query.MessageType.IsNotEmpty())
        {
            builder.Append($" AND {DatabaseConstants.MessageType} = ");
            builder.AppendParameter(query.MessageType);
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            builder.Append($" AND {DatabaseConstants.ReceivedAt} = ");
            builder.AppendParameter(query.ReceivedAt);
        }

        if (query.Replayable.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(query.Replayable.Value ? 1 : 0); // Oracle uses NUMBER(1) for bool
        }

        if (query.MessageIds is { Length: > 0 })
        {
            builder.Append(" AND id IN (");
            for (var i = 0; i < query.MessageIds.Length; i++)
            {
                if (i > 0) builder.Append(", ");
                builder.AppendParameter(query.MessageIds[i]);
            }

            builder.Append(")");
        }
    }
}
