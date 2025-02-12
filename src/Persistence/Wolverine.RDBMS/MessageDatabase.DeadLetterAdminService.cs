using System.Data;
using Wolverine.Persistence.Durability;
using JasperFx.Core;
using Weasel.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.RDBMS.Durability;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T> : IDeadLetterAdminService
{
    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append($"select {DatabaseConstants.ReceivedAt}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExceptionType}, count(*) as total");
        builder.Append($" from {SchemaName}.{DatabaseConstants.DeadLetterTable}");
        builder.Append(" where 1 = 1");

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
        
        var cmd = builder.Compile();

        var envelopes = new List<DeadLetterQueueCount>();
        
        await using var conn = await DataSource.OpenConnectionAsync(_cancellation);
        try
        {
            cmd.Connection = conn;
            await using var reader = await cmd.ExecuteReaderAsync(_cancellation).ConfigureAwait(false);

            while (await reader.ReadAsync(token))
            {
                var uri = new Uri(await reader.GetFieldValueAsync<string>(0, token));
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

    public Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeByDatabaseAsync(string serviceName,
        Uri database,
        TimeRange range,
        CancellationToken token)
    {
        return SummarizeAllAsync(serviceName, range, token);
    }

    protected virtual string toTopClause(DeadLetterEnvelopeQuery query)
    {
        return "";
    }
    
    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        var topSelect = toTopClause(query);
        
        builder.Append($"select{topSelect} {DatabaseConstants.DeadLetterFields}, count(*) OVER() as total_rows from {SchemaName}.{DatabaseConstants.DeadLetterTable} where 1 = 1");

        writeDeadLetterWhereClause(query, builder);

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

        var cmd = builder.Compile();
        cmd.Connection = conn;

        await using var reader = await cmd.ExecuteReaderAsync(token);

        var results = new DeadLetterEnvelopeResults{PageNumber = query.PageNumber};
        if (await reader.ReadAsync(token))
        {
            var env = await DatabasePersistence.ReadDeadLetterAsync(reader, token);
            results.Envelopes.Add(env);
            results.TotalCount = await reader.GetFieldValueAsync<int>(10, token);
        }

        while (await reader.ReadAsync(token))
        {
            var env = await DatabasePersistence.ReadDeadLetterAsync(reader, token);
            results.Envelopes.Add(env);
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return results;
    }

    private static void writeDeadLetterWhereClause(DeadLetterEnvelopeQuery query, DbCommandBuilder builder)
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
    }

    protected abstract void writePagingAfter(DbCommandBuilder builder, int offset, int limit);

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        
        builder.Append($"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where 1 = 1");

        writeDeadLetterWhereClause(query, builder);

        return executeCommandBatch(builder, token);
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        builder.Append(
            $"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = ");
        builder.AppendParameter(true);
        builder.Append(" where 1 = 1");
        writeDeadLetterWhereClause(query, builder);
        builder.Append(';');
        builder.Append(
            $"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Replayable} = ");
        builder.AppendParameter(true);
        builder.Append(';');
        
        return executeCommandBatch(builder, token);
    }

    public Task DiscardAsync(MessageBatchRequest request, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        foreach (var id in request.Ids)
        {
            builder.Append($"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Id} = ");
            builder.AppendParameter(id);
            builder.Append(';');
        }
        
        new MoveReplayableErrorMessagesToIncomingOperation(this).ConfigureCommand(builder);
        
        return executeCommandBatch(builder, token);
    }

    public Task ReplayAsync(MessageBatchRequest request, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        foreach (var id in request.Ids)
        {
            builder.Append(
                $"update {SchemaName}.{DatabaseConstants.DeadLetterTable} set {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(true);
            builder.Append($" where {DatabaseConstants.Id} = ");
            builder.AppendParameter(id);
            builder.Append(';');
            builder.Append(
                $"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where {DatabaseConstants.Replayable} = ");
            builder.AppendParameter(true);
            builder.Append(';');
        }
        
        new MoveReplayableErrorMessagesToIncomingOperation(this).ConfigureCommand(builder);
        
        return executeCommandBatch(builder, token);
    }
}