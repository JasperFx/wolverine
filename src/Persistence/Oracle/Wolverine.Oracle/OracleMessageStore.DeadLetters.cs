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
            $"SELECT {DatabaseConstants.DeadLetterQueryFields} FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE id = :id");
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
        // GH-5209: archived dead letters are left out of summaries
        builder.Append($" WHERE {DatabaseConstants.ArchivedAt} IS NULL");

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

    public DeadLetterCapabilities Capabilities => DeadLetterCapabilities.All;

    public async Task<IReadOnlyList<DeadLetterGroupSummary>> SummarizeAllAsync(string serviceName,
        DeadLetterSummaryOptions options, CancellationToken token)
    {
        var query = options.Query;
        if (query.HasHeaderFilter())
        {
            var matches = new List<DeadLetterEnvelope>();
            await foreach (var envelope in readHeaderMatchesAsync(query, query.EffectiveArchiveFilter(), token))
            {
                matches.Add(envelope);
            }

            return DeadLetterSummaries.Group(matches, serviceName, Uri, options.Grouping);
        }

        var keys = DatabasePersistence.DeadLetterGroupingColumns(options.Grouping);

        var builder = ToOracleCommandBuilder();
        builder.Append($"SELECT {keys}, {DatabasePersistence.DeadLetterSummaryAggregates}");
        builder.Append($" FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");
        writeDeadLetterWhereClause(query, builder, query.EffectiveArchiveFilter());
        builder.Append($" GROUP BY {keys}");

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        var list = new List<DeadLetterGroupSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        var offset = DatabasePersistence.DeadLetterGroupingColumnCount(options.Grouping);
        while (await reader.ReadAsync(token))
        {
            var key = await reader.IsDBNullAsync(0, token) ? null : Convert.ToString(reader.GetValue(0));
            list.Add(await DatabasePersistence.ReadDeadLetterGroupSummaryAsync(reader, offset, serviceName,
                options.Grouping, key, Uri, token));
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return list.OrderByDescending(x => x.Count).ToList();
    }

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            return await queryWithHeaderFilterAsync(query, token);
        }

        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"SELECT {DatabaseConstants.DeadLetterQueryFields}, COUNT(*) OVER() as total_rows FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");

        writeDeadLetterWhereClause(query, builder, query.EffectiveArchiveFilter());

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
            results.TotalCount = Convert.ToInt32(reader.GetValue(DatabaseConstants.DeadLetterQueryFieldCount));
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

        await foreach (var envelope in readHeaderMatchesAsync(query, query.EffectiveArchiveFilter(), token))
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
        DeadLetterArchiveFilter archived,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();
        builder.Append(
            $"SELECT {DatabaseConstants.DeadLetterQueryFields} FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");
        writeDeadLetterWhereClause(query, builder, archived);
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
    private async Task<List<Guid>> findHeaderMatchIdsAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, CancellationToken token)
    {
        var ids = new List<Guid>();
        await foreach (var envelope in readHeaderMatchesAsync(query, archived, token))
        {
            ids.Add(envelope.Id);
        }

        return ids;
    }

    // Oracle caps an IN list at 1000 expressions
    private const int HeaderFilterIdChunkSize = 500;

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return DiscardMatchingAsync(query, token);
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return ReplayMatchingAsync(query, token);
    }

    public Task<DeadLetterOperationResult> DiscardMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, query.EffectiveArchiveFilter(), builder =>
        {
            builder.Append($"DELETE FROM {SchemaName}.{DatabaseConstants.DeadLetterTable} WHERE 1 = 1");
        }, token);
    }

    // An archived dead letter has to be restored before it can be replayed
    public Task<DeadLetterOperationResult> ReplayMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude, builder =>
        {
            builder.Append(
                $"UPDATE {SchemaName}.{DatabaseConstants.DeadLetterTable} SET {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(1); // Oracle uses NUMBER(1) for bool
            builder.Append(" WHERE 1 = 1");
        }, token);
    }

    public Task<DeadLetterOperationResult> ArchiveMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude, builder =>
        {
            builder.Append(
                $"UPDATE {SchemaName}.{DatabaseConstants.DeadLetterTable} SET {DatabaseConstants.ArchivedAt} = ");
            builder.AppendParameter(now);
            builder.Append($", {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(0); // Oracle uses NUMBER(1) for bool
            builder.Append(" WHERE 1 = 1");
        }, token);
    }

    public Task<DeadLetterOperationResult> RestoreMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Only, builder =>
        {
            builder.Append(
                $"UPDATE {SchemaName}.{DatabaseConstants.DeadLetterTable} SET {DatabaseConstants.ArchivedAt} = NULL WHERE 1 = 1");
        }, token);
    }

    // GH-5209. One set-based statement over the matching rows, reporting the affected row count. A header filter
    // is resolved to the matching ids first and the statement runs against chunks of them, summing the counts.
    // No match means no statement at all -- never fall through to an unfiltered one.
    private async Task<DeadLetterOperationResult> executeMatchingAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, Action<Weasel.Oracle.CommandBuilder> writeStatement, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            var ids = await findHeaderMatchIdsAsync(query, archived, token);
            var affected = 0;
            foreach (var chunk in ids.Chunk(HeaderFilterIdChunkSize))
            {
                affected += await executeStatementAsync(new DeadLetterEnvelopeQuery { MessageIds = chunk }, archived,
                    writeStatement, token);
            }

            return new DeadLetterOperationResult(affected);
        }

        return new DeadLetterOperationResult(await executeStatementAsync(query, archived, writeStatement, token));
    }

    private async Task<int> executeStatementAsync(DeadLetterEnvelopeQuery query, DeadLetterArchiveFilter archived,
        Action<Weasel.Oracle.CommandBuilder> writeStatement, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();
        writeStatement(builder);
        writeDeadLetterWhereClause(query, builder, archived);

        await using var cmd = builder.Compile();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            return await cmd.ExecuteNonQueryAsync(token);
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

    private void writeDeadLetterWhereClause(DeadLetterEnvelopeQuery query, Weasel.Oracle.CommandBuilder builder,
        DeadLetterArchiveFilter archived)
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
            switch (query.ExceptionMessageMatch)
            {
                case DeadLetterMessageMatch.Exact:
                    builder.Append($" AND {DatabaseConstants.ExceptionMessage} = ");
                    builder.AppendParameter(query.ExceptionMessage);
                    break;

                case DeadLetterMessageMatch.Contains:
                    builder.Append($" AND {DatabaseConstants.ExceptionMessage} LIKE ");
                    builder.AppendParameter(DeadLetterSummaries.ContainsPattern(query.ExceptionMessage, escapeBrackets: false));
                    builder.Append(DeadLetterSummaries.LikeEscapeClause);
                    break;

                default:
                    // The original behavior: the value is the LIKE pattern
                    builder.Append($" AND {DatabaseConstants.ExceptionMessage} LIKE ");
                    builder.AppendParameter(query.ExceptionMessage);
                    break;
            }
        }

        if (query.FailureGroup.IsNotEmpty())
        {
            if (DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out var legacyType))
            {
                builder.Append($" AND {DatabaseConstants.FailureGroup} IS NULL AND {DatabaseConstants.ExceptionType} = ");
                builder.AppendParameter(legacyType);
            }
            else
            {
                builder.Append($" AND {DatabaseConstants.FailureGroup} = ");
                builder.AppendParameter(query.FailureGroup);
            }
        }

        switch (archived)
        {
            case DeadLetterArchiveFilter.Exclude:
                builder.Append($" AND {DatabaseConstants.ArchivedAt} IS NULL");
                break;
            case DeadLetterArchiveFilter.Only:
                builder.Append($" AND {DatabaseConstants.ArchivedAt} IS NOT NULL");
                break;
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
