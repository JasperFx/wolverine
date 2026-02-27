using Wolverine.Oracle.Util;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.RDBMS;

namespace Wolverine.Oracle;

internal partial class OracleMessageStore
{
    public IScheduledMessages ScheduledMessages => this;

    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"SELECT {DatabaseConstants.Id}, {DatabaseConstants.MessageType}, {DatabaseConstants.ExecutionTime}, {DatabaseConstants.ReceivedAt}, {DatabaseConstants.Attempts}, COUNT(*) OVER() as total_rows FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        writeScheduledMessageWhereClause(query, builder);

        builder.Append($" ORDER BY {DatabaseConstants.ExecutionTime}");

        if (query.PageNumber <= 0) query.PageNumber = 1;

        if (query.PageSize > 0)
        {
            var offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * query.PageSize;
            builder.Append($" OFFSET {offset} ROWS FETCH NEXT {query.PageSize} ROWS ONLY");
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
                Id = OracleEnvelopeReader.ReadGuid(reader, 0),
                MessageType = await reader.GetFieldValueAsync<string>(1, token),
                Attempts = Convert.ToInt32(reader.GetValue(4))
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
                results.TotalCount = Convert.ToInt32(reader.GetValue(5));
            }
        }

        await reader.CloseAsync();
        await conn.CloseAsync();

        return results;
    }

    async Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"DELETE FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        writeScheduledMessageWhereClause(query, builder);

        var cmd = builder.Compile();

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

    async Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();

        builder.Append(
            $"UPDATE {SchemaName}.{DatabaseConstants.IncomingTable} SET {DatabaseConstants.ExecutionTime} = ");
        builder.AppendParameter(newExecutionTime.ToUniversalTime());
        builder.Append($" WHERE {DatabaseConstants.Id} = ");
        builder.AppendParameter(envelopeId);
        builder.Append($" AND {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}'");

        var cmd = builder.Compile();

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

    async Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName, CancellationToken token)
    {
        var builder = ToOracleCommandBuilder();
        builder.Append(
            $"SELECT {DatabaseConstants.MessageType}, COUNT(*) as total FROM {SchemaName}.{DatabaseConstants.IncomingTable} WHERE {DatabaseConstants.Status} = '{EnvelopeStatus.Scheduled}' GROUP BY {DatabaseConstants.MessageType}");

        var cmd = builder.Compile();

        var results = new List<ScheduledMessageCount>();

        await using var conn = await _dataSource.OpenConnectionAsync(token);
        try
        {
            cmd.Connection = conn;
            await using var reader = await cmd.ExecuteReaderAsync(token);

            while (await reader.ReadAsync(token))
            {
                var messageType = await reader.GetFieldValueAsync<string>(0, token);
                var count = Convert.ToInt32(await reader.GetFieldValueAsync<decimal>(1, token));
                results.Add(new ScheduledMessageCount(serviceName, messageType, Uri, count));
            }
        }
        finally
        {
            await conn.CloseAsync();
        }

        return results;
    }

    private void writeScheduledMessageWhereClause(ScheduledMessageQuery query, Weasel.Oracle.CommandBuilder builder)
    {
        if (query.MessageType is not null)
        {
            builder.Append($" AND {DatabaseConstants.MessageType} = ");
            builder.AppendParameter(query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.ExecutionTime} >= ");
            builder.AppendParameter(query.ExecutionTimeFrom.Value.ToUniversalTime());
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            builder.Append($" AND {DatabaseConstants.ExecutionTime} <= ");
            builder.AppendParameter(query.ExecutionTimeTo.Value.ToUniversalTime());
        }

        if (query.MessageIds.Length > 0)
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
