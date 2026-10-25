using System.Data;
using Wolverine.Persistence.Durability;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    // GH-3166: sentinel "received at" for dead letters whose envelope had no Destination (received_at is NULL).
    internal static readonly Uri UnknownReceivedAtUri = new("unknown://none");

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append($"select {DatabaseConstants.ReceivedAt}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExceptionType}, count(*) as total");
        builder.Append($" from {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)}");
        // GH-5209: archived dead letters are left out of summaries
        builder.Append($" where {DatabaseConstants.ArchivedAt} is null");

        if (range.From.HasValue)
        {
            builder.Append($" and {DatabaseConstants.SentAt} >= @from");
            builder.AddNamedParameter("from", range.From.Value.ToUniversalTime(), DbType.DateTimeOffset);
        }
        
        if (range.To.HasValue)
        {
            builder.Append($" and {DatabaseConstants.SentAt} <= @to");
            builder.AddNamedParameter("to", range.To.Value.ToUniversalTime(), DbType.DateTimeOffset);
        }
        
        builder.Append($" group by {DatabaseConstants.ReceivedAt}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExceptionType}");
        
        await using var cmd = builder.Compile();

        var envelopes = new List<DeadLetterQueueCount>();
        
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
        try
        {
            cmd.Connection = conn;
            await using var reader = await cmd.ExecuteReaderAsync(_cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(token))
            {
                // GH-3166: received_at is envelope.Destination?.ToString() and is NULL for any envelope
                // that dead-lettered without a destination. An unguarded new Uri(GetFieldValueAsync<string>)
                // threw on DBNull and aborted the ENTIRE summarize — so a single destination-less dead
                // letter made the DLQ explorer report "No dead letter queue entries found" for the whole
                // store, even though count(*) (the durability monitor's path) reported hundreds. Group the
                // destination-less rows under a clear sentinel instead of throwing.
                var uri = await reader.IsDBNullAsync(0, token)
                    ? UnknownReceivedAtUri
                    : new Uri(await reader.GetFieldValueAsync<string>(0, token));
                var messageType = await reader.GetFieldValueAsync<string>(1, token);
                var exceptionType = await reader.GetFieldValueAsync<string>(2, token);
                var count = await reader.GetFieldValueAsync<int>(3, token);
                
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

        // Rows recorded before failure fingerprinting have no failure group, and are grouped by exception type
        var keys = DatabasePersistence.DeadLetterGroupingColumns(options.Grouping);

        var builder = ToCommandBuilder();
        builder.Append($"select {keys}, {DatabasePersistence.DeadLetterSummaryAggregates}");
        builder.Append($" from {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} where 1 = 1");
        writeDeadLetterWhereClause(query, builder, query.EffectiveArchiveFilter());
        builder.Append($" group by {keys}");

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

    protected virtual string toTopClause(DeadLetterEnvelopeQuery query)
    {
        return "";
    }
    
    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            return await queryWithHeaderFilterAsync(query, token);
        }

        var builder = ToCommandBuilder();

        var topSelect = toTopClause(query);
        
        builder.Append($"select{topSelect} {DatabaseConstants.DeadLetterQueryFields}, count(*) OVER() as total_rows from {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} where 1 = 1");

        writeDeadLetterWhereClause(query, builder, query.EffectiveArchiveFilter());

        builder.Append(" order by ");
        builder.Append(DatabaseConstants.ExecutionTime);

        if (query.PageNumber <= 0) query.PageNumber = 1;
        
        if (query.PageSize > 0)
        {
            var offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * query.PageSize;
            writePagingAfter(builder, offset, query.PageSize);
        }

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var results = new DeadLetterEnvelopeResults{PageNumber = query.PageNumber};
        if (await reader.ReadAsync(token))
        {
            var env = await DatabasePersistence.ReadDeadLetterAsync(reader, token, Logger);
            results.Envelopes.Add(env);
            results.TotalCount = Convert.ToInt32(reader.GetValue(DatabaseConstants.DeadLetterQueryFieldCount));
        }

        while (await reader.ReadAsync(token))
        {
            var env = await DatabasePersistence.ReadDeadLetterAsync(reader, token, Logger);
            results.Envelopes.Add(env);
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return results;
    }

    // GH-5164: headers only exist inside the serialized envelope body, so a header filter cannot be
    // a SQL predicate. Every row matching the rest of the query is read in the normal order and the
    // header criteria applied in memory. Paging and the total count are computed over the rows that
    // match the headers, so a page is never short or padded with non-matching rows.
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
        var builder = ToCommandBuilder();
        builder.Append($"select {DatabaseConstants.DeadLetterQueryFields} from {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} where 1 = 1");
        writeDeadLetterWhereClause(query, builder, archived);
        // id breaks ties so that separate page requests see one stable order
        builder.Append($" order by {DatabaseConstants.ExecutionTime}, {DatabaseConstants.Id}");

        await using var conn = CreateConnection();
        await conn.OpenAsync(token);

        await using var cmd = builder.Compile();
        cmd.Connection = conn;

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var envelope = await DatabasePersistence.ReadDeadLetterAsync(reader, token, Logger);
            if (query.MatchesHeaders(envelope.Envelope))
            {
                yield return envelope;
            }
        }

        await reader.CloseAsync();
        await conn.CloseAsync();
    }

    // Resolves a header-filtered query to the ids of the rows it matches. An empty result means
    // "nothing matched" -- callers must not fall through to an unfiltered statement.
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

    private const int HeaderFilterIdChunkSize = 500;

    private void writeDeadLetterWhereClause(DeadLetterEnvelopeQuery query, DbCommandBuilder builder,
        DeadLetterArchiveFilter archived)
    {
        if (query.Range.From.HasValue)
        {
            builder.Append($" and {DatabaseConstants.SentAt} >= ");
            builder.AppendParameter(query.Range.From.Value.ToUniversalTime());
        }
        
        if (query.Range.To.HasValue)
        {
            builder.Append($" and {DatabaseConstants.SentAt} <= ");
            builder.AppendParameter(query.Range.To.Value.ToUniversalTime());
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            builder.Append($" and {DatabaseConstants.ExceptionType} = ");
            builder.AppendParameter(query.ExceptionType);
        }

        if (query.ExceptionMessage.IsNotEmpty())
        {
            switch (query.ExceptionMessageMatch)
            {
                case DeadLetterMessageMatch.Exact:
                    builder.Append($" and {DatabaseConstants.ExceptionMessage} = ");
                    builder.AppendParameter(query.ExceptionMessage);
                    break;

                case DeadLetterMessageMatch.Contains:
                    builder.Append($" and {DatabaseConstants.ExceptionMessage} LIKE ");
                    builder.AppendParameter(DeadLetterSummaries.ContainsPattern(query.ExceptionMessage));
                    builder.Append(DeadLetterSummaries.LikeEscapeClause);
                    break;

                default:
                    // The original behavior: the value is the LIKE pattern
                    builder.Append($" and {DatabaseConstants.ExceptionMessage} LIKE ");
                    builder.AppendParameter(query.ExceptionMessage);
                    break;
            }
        }

        if (query.FailureGroup.IsNotEmpty())
        {
            if (DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out var legacyType))
            {
                builder.Append($" and {DatabaseConstants.FailureGroup} is null and {DatabaseConstants.ExceptionType} = ");
                builder.AppendParameter(legacyType);
            }
            else
            {
                builder.Append($" and {DatabaseConstants.FailureGroup} = ");
                builder.AppendParameter(query.FailureGroup);
            }
        }

        switch (archived)
        {
            case DeadLetterArchiveFilter.Exclude:
                builder.Append($" and {DatabaseConstants.ArchivedAt} is null");
                break;
            case DeadLetterArchiveFilter.Only:
                builder.Append($" and {DatabaseConstants.ArchivedAt} is not null");
                break;
        }

        if (query.MessageType.IsNotEmpty())
        {
            builder.Append($" and {DatabaseConstants.MessageType} = ");
            builder.AppendParameter(query.MessageType);
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            builder.Append($" and {DatabaseConstants.ReceivedAt} = ");
            builder.AppendParameter(query.ReceivedAt);
        }

        if (query.Replayable.HasValue)
        {
            builder.Append($" and {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(query.Replayable.Value);
        }

        if (query.MessageIds != null && query.MessageIds.Any())
        {
            writeMessageIdArrayQueryList(builder, query.MessageIds);
        }
    }

    protected abstract void writeMessageIdArrayQueryList(DbCommandBuilder builder, Guid[] messageIds);

    protected abstract void writePagingAfter(DbCommandBuilder builder, int offset, int limit);

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
            builder.Append($"delete from {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} where 1 = 1");
        }, token);
    }

    // An archived dead letter has to be restored before it can be replayed
    public Task<DeadLetterOperationResult> ReplayMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude, builder =>
        {
            builder.Append(
                $"update {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} set {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(true);
            builder.Append(" where 1 = 1");
        }, token);
    }

    public Task<DeadLetterOperationResult> ArchiveMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude, builder =>
        {
            builder.Append(
                $"update {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} set {DatabaseConstants.ArchivedAt} = ");
            builder.AppendParameter(now);
            builder.Append($", {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(false);
            builder.Append(" where 1 = 1");
        }, token);
    }

    public Task<DeadLetterOperationResult> RestoreMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Only, builder =>
        {
            builder.Append(
                $"update {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} set {DatabaseConstants.ArchivedAt} = null where 1 = 1");
        }, token);
    }

    // GH-5209. Runs one set-based statement over the rows the query matches and reports the affected row count.
    // A header filter cannot be a SQL predicate, so the matching ids are found first and the statement runs
    // against chunks of them, summing the counts. No match means no statement at all -- never fall through to
    // an unfiltered one.
    private async Task<DeadLetterOperationResult> executeMatchingAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, Action<DbCommandBuilder> writeStatement, CancellationToken token)
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
        Action<DbCommandBuilder> writeStatement, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        writeStatement(builder);
        writeDeadLetterWhereClause(query, builder, archived);

        await using var cmd = builder.Compile();
        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        // Read the current serialized envelope, replace its Data, and re-serialize
        var deadLetter = await DeadLetterEnvelopeByIdAsync(envelopeId);
        if (deadLetter == null) return;

        deadLetter.Envelope.Data = newBody;
        var serialized = EnvelopeSerializer.Serialize(deadLetter.Envelope);

        var builder = ToCommandBuilder();
        builder.Append(
            $"update {QuotedTableNameFor(DatabaseConstants.DeadLetterTable)} set {DatabaseConstants.Body} = ");
        builder.AppendParameter(serialized);
        builder.Append($", {DatabaseConstants.Replayable} = ");
        builder.AppendParameter(true);
        builder.Append($" where {DatabaseConstants.Id} = ");
        builder.AppendParameter(envelopeId);
        builder.Append(';');

        await executeCommandBatch(builder, token);
    }

}