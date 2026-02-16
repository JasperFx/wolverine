using System.Net;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IDeadLetters
{
    private static string DlqId(Guid id)
    {
        return $"deadletter|{id}";
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        try
        {
            var response = await _container.ReadItemAsync<DeadLetterMessage>(DlqId(id),
                new PartitionKey(DocumentTypes.DeadLetterPartition));
            return response.Resource.ToEnvelope();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token)
    {
        var conditions = new List<string> { "c.docType = @docType" };

        if (range.From.HasValue)
        {
            conditions.Add("c.sentAt >= @from");
        }

        if (range.To.HasValue)
        {
            conditions.Add("c.sentAt <= @to");
        }

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)}";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.DeadLetter);
        if (range.From.HasValue) query = query.WithParameter("@from", range.From.Value);
        if (range.To.HasValue) query = query.WithParameter("@to", range.To.Value);

        var messages = new List<DeadLetterMessage>();
        using var iterator = _container.GetItemQueryIterator<DeadLetterMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.DeadLetterPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            messages.AddRange(response);
        }

        var grouped = messages
            .GroupBy(x => new { ReceivedAt = x.ReceivedAt ?? "", x.MessageType, x.ExceptionType })
            .Select(g => new DeadLetterQueueCount(
                serviceName,
                g.Key.ReceivedAt.IsNotEmpty() ? new Uri(g.Key.ReceivedAt) : Uri,
                g.Key.MessageType ?? "",
                g.Key.ExceptionType ?? "",
                Uri,
                g.Count()))
            .ToList();

        return grouped;
    }

    public async Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            var envelopes = new List<DeadLetterEnvelope>();
            foreach (var id in query.MessageIds)
            {
                try
                {
                    var response = await _container.ReadItemAsync<DeadLetterMessage>(DlqId(id),
                        new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
                    envelopes.Add(response.Resource.ToEnvelope());
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    // Skip missing
                }
            }

            return new DeadLetterEnvelopeResults
            {
                PageNumber = 1,
                TotalCount = envelopes.Count,
                Envelopes = envelopes,
                DatabaseUri = Uri
            };
        }

        var conditions = new List<string> { "c.docType = @docType" };

        if (query.Range?.From.HasValue == true)
        {
            conditions.Add("c.sentAt >= @from");
        }

        if (query.Range?.To.HasValue == true)
        {
            conditions.Add("c.sentAt <= @to");
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            conditions.Add("c.exceptionType = @exceptionType");
        }

        if (query.ExceptionMessage.IsNotEmpty())
        {
            conditions.Add("STARTSWITH(c.exceptionMessage, @exceptionMessage)");
        }

        if (query.MessageType.IsNotEmpty())
        {
            conditions.Add("c.messageType = @messageType");
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            conditions.Add("c.receivedAt = @receivedAt");
        }

        var whereClause = string.Join(" AND ", conditions);

        // Count query
        var countQueryText = $"SELECT VALUE COUNT(1) FROM c WHERE {whereClause}";
        var countQuery = RebuildQueryDef(countQueryText, query);
        int totalCount;
        using (var countIterator = _container.GetItemQueryIterator<int>(countQuery,
                   requestOptions: new QueryRequestOptions
                   {
                       PartitionKey = new PartitionKey(DocumentTypes.DeadLetterPartition)
                   }))
        {
            var countResponse = await countIterator.ReadNextAsync(token);
            totalCount = countResponse.FirstOrDefault();
        }

        // Paged query
        if (query.PageNumber <= 0) query.PageNumber = 1;
        var skip = (query.PageNumber - 1) * query.PageSize;

        var pagedQueryText =
            $"SELECT * FROM c WHERE {whereClause} ORDER BY c.sentAt OFFSET @skip LIMIT @take";
        var pagedQuery = RebuildQueryDef(pagedQueryText, query)
            .WithParameter("@skip", skip)
            .WithParameter("@take", query.PageSize);

        var messages = new List<DeadLetterMessage>();
        using var pagedIterator = _container.GetItemQueryIterator<DeadLetterMessage>(pagedQuery,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.DeadLetterPartition)
            });

        while (pagedIterator.HasMoreResults)
        {
            var response = await pagedIterator.ReadNextAsync(token);
            messages.AddRange(response);
        }

        return new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            Envelopes = messages.Select(m => m.ToEnvelope()).ToList(),
            DatabaseUri = Uri
        };
    }

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            foreach (var id in query.MessageIds)
            {
                try
                {
                    await _container.DeleteItemAsync<DeadLetterMessage>(DlqId(id),
                        new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    // Already gone
                }
            }

            return;
        }

        var messages = await LoadDeadLettersByQuery(query, token);
        foreach (var message in messages)
        {
            try
            {
                await _container.DeleteItemAsync<DeadLetterMessage>(message.Id,
                    new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }
    }

    public async Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            foreach (var id in query.MessageIds)
            {
                await MarkDeadLetterAsReplayableAsync(DlqId(id), token);
            }

            return;
        }

        var messages = await LoadDeadLettersByQuery(query, token);
        foreach (var message in messages)
        {
            message.Replayable = true;
            await _container.ReplaceItemAsync(message, message.Id,
                new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
        }
    }

    public async Task MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "")
    {
        var query = new DeadLetterEnvelopeQuery
        {
            Range = TimeRange.AllTime(),
            ExceptionType = exceptionType.IsEmpty() ? null : exceptionType
        };
        await ReplayAsync(query, CancellationToken.None);
    }

    public async Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        await ReplayAsync(new DeadLetterEnvelopeQuery { MessageIds = ids }, CancellationToken.None);
    }

    private async Task MarkDeadLetterAsReplayableAsync(string id, CancellationToken token)
    {
        try
        {
            var response = await _container.ReadItemAsync<DeadLetterMessage>(id,
                new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
            var message = response.Resource;
            message.Replayable = true;
            await _container.ReplaceItemAsync(message, id,
                new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    private async Task<List<DeadLetterMessage>> LoadDeadLettersByQuery(DeadLetterEnvelopeQuery query,
        CancellationToken token)
    {
        var conditions = new List<string> { "c.docType = @docType" };

        if (query.Range.From.HasValue) conditions.Add("c.sentAt >= @from");
        if (query.Range.To.HasValue) conditions.Add("c.sentAt <= @to");
        if (query.ExceptionType.IsNotEmpty()) conditions.Add("c.exceptionType = @exceptionType");
        if (query.MessageType.IsNotEmpty()) conditions.Add("c.messageType = @messageType");
        if (query.ReceivedAt.IsNotEmpty()) conditions.Add("c.receivedAt = @receivedAt");

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)}";
        var queryDef = RebuildQueryDef(queryText, query);

        var messages = new List<DeadLetterMessage>();
        using var iterator = _container.GetItemQueryIterator<DeadLetterMessage>(queryDef,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(DocumentTypes.DeadLetterPartition)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            messages.AddRange(response);
        }

        return messages;
    }

    private QueryDefinition RebuildQueryDef(string queryText, DeadLetterEnvelopeQuery query)
    {
        var queryDef = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.DeadLetter);

        if (query.Range?.From.HasValue == true) queryDef = queryDef.WithParameter("@from", query.Range.From.Value);
        if (query.Range?.To.HasValue == true) queryDef = queryDef.WithParameter("@to", query.Range.To.Value);
        if (query.ExceptionType.IsNotEmpty())
            queryDef = queryDef.WithParameter("@exceptionType", query.ExceptionType);
        if (query.ExceptionMessage.IsNotEmpty())
            queryDef = queryDef.WithParameter("@exceptionMessage", query.ExceptionMessage);
        if (query.MessageType.IsNotEmpty()) queryDef = queryDef.WithParameter("@messageType", query.MessageType);
        if (query.ReceivedAt.IsNotEmpty()) queryDef = queryDef.WithParameter("@receivedAt", query.ReceivedAt);

        return queryDef;
    }
}
