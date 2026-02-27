using Weasel.Core;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    public IScheduledMessages ScheduledMessages => this;

    public async Task<ScheduledMessageResults> QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        var topSelect = toTopClause(query);

        // Columns: 0=id, 1=message_type, 2=execution_time, 3=received_at, 4=attempts, 5=total_rows
        builder.Append(
            $"select{topSelect} {DatabaseConstants.Id}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.ReceivedAt}, {DatabaseConstants.Attempts}, count(*) OVER() as total_rows from {SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        writeScheduledMessageWhereClause(query, builder);

        builder.Append($" order by {DatabaseConstants.ExecutionTime}");

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

        var results = new ScheduledMessageResults { PageNumber = query.PageNumber, DatabaseUri = Uri };

        while (await reader.ReadAsync(token))
        {
            var summary = new ScheduledMessageSummary
            {
                Id = await reader.GetFieldValueAsync<Guid>(0, token),
                MessageType = await reader.GetFieldValueAsync<string>(1, token),
                Attempts = await reader.GetFieldValueAsync<int>(4, token)
            };

            if (!await reader.IsDBNullAsync(2, token))
            {
                summary.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(2, token);
            }

            if (!await reader.IsDBNullAsync(3, token))
            {
                summary.Destination = await reader.GetFieldValueAsync<string>(3, token);
            }

            results.Messages.Add(summary);

            if (results.TotalCount == 0)
            {
                results.TotalCount = await reader.GetFieldValueAsync<int>(5, token);
            }
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return results;
    }

    public async Task CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        builder.Append(
            $"delete from {SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        writeScheduledMessageWhereClause(query, builder);

        var cmd = builder.Compile();

        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        var builder = ToCommandBuilder();

        builder.Append(
            $"update {SchemaName}.{DatabaseConstants.IncomingTable} set {DatabaseConstants.ExecutionTime} = ");
        builder.AppendParameter(newExecutionTime.ToUniversalTime());
        builder.Append($" where {DatabaseConstants.Id} = ");
        builder.AppendParameter(envelopeId);
        builder.Append($" and {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        var cmd = builder.Compile();

        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    public async Task<IReadOnlyList<ScheduledMessageCount>> SummarizeAsync(string serviceName, CancellationToken token)
    {
        var builder = ToCommandBuilder();
        builder.Append(
            $"select {DatabaseConstants.MessageType}, count(*) as total from {SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' group by {DatabaseConstants.MessageType}");

        var cmd = builder.Compile();

        var results = new List<ScheduledMessageCount>();

        await using var conn = await DataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token))
            {
                var messageType = await reader.GetFieldValueAsync<string>(0, token);
                var count = await reader.GetFieldValueAsync<int>(1, token);
                results.Add(new ScheduledMessageCount(serviceName, messageType, Uri, count));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }

        return results;
    }

    protected virtual string toTopClause(ScheduledMessageQuery query)
    {
        return "";
    }

    private void writeScheduledMessageWhereClause(ScheduledMessageQuery query, DbCommandBuilder builder)
    {
        if (query.MessageType is not null)
        {
            builder.Append($" and {DatabaseConstants.MessageType} = ");
            builder.AppendParameter(query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            builder.Append($" and {DatabaseConstants.ExecutionTime} >= ");
            builder.AppendParameter(query.ExecutionTimeFrom.Value.ToUniversalTime());
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            builder.Append($" and {DatabaseConstants.ExecutionTime} <= ");
            builder.AppendParameter(query.ExecutionTimeTo.Value.ToUniversalTime());
        }

        if (query.MessageIds.Length > 0)
        {
            writeMessageIdArrayQueryList(builder, query.MessageIds);
        }
    }
}
