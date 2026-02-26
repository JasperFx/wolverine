using System.Net;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IScheduledMessages
{
    public IScheduledMessages ScheduledMessages => this;

    async Task<ScheduledMessageResults> IScheduledMessages.QueryAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        var conditions = new List<string> { "c.docType = @docType", "c.status = @status" };

        if (query.MessageIds.Length > 0)
        {
            conditions.Add("ARRAY_CONTAINS(@messageIds, c.envelopeId)");
        }

        if (query.MessageType.IsNotEmpty())
        {
            conditions.Add("c.messageType = @messageType");
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            conditions.Add("c.executionTime >= @execFrom");
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            conditions.Add("c.executionTime <= @execTo");
        }

        // Count query
        var countQueryText = $"SELECT VALUE COUNT(1) FROM c WHERE {string.Join(" AND ", conditions)}";
        var countQuery = BuildScheduledQuery(countQueryText, query);

        int totalCount = 0;
        using (var countIterator = _container.GetItemQueryIterator<int>(countQuery))
        {
            while (countIterator.HasMoreResults)
            {
                var response = await countIterator.ReadNextAsync(token);
                totalCount += response.Sum();
            }
        }

        // Data query
        if (query.PageNumber <= 0) query.PageNumber = 1;
        var offset = (query.PageNumber - 1) * query.PageSize;
        var dataQueryText =
            $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)} ORDER BY c.executionTime OFFSET {offset} LIMIT {query.PageSize}";
        var dataQuery = BuildScheduledQuery(dataQueryText, query);

        var messages = new List<IncomingMessage>();
        using (var iterator = _container.GetItemQueryIterator<IncomingMessage>(dataQuery))
        {
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(token);
                messages.AddRange(response);
            }
        }

        return new ScheduledMessageResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            DatabaseUri = Uri,
            Messages = messages.Select(m => new ScheduledMessageSummary
            {
                Id = m.EnvelopeId,
                MessageType = m.MessageType,
                ScheduledTime = m.ExecutionTime,
                Destination = m.ReceivedAt,
                Attempts = m.Attempts
            }).ToList()
        };
    }

    async Task IScheduledMessages.CancelAsync(ScheduledMessageQuery query, CancellationToken token)
    {
        if (query.MessageIds.Length > 0)
        {
            foreach (var messageId in query.MessageIds)
            {
                // Query for the incoming message with this envelope ID
                var findQuery = new QueryDefinition(
                        "SELECT * FROM c WHERE c.docType = @docType AND c.status = @status AND c.envelopeId = @envelopeId")
                    .WithParameter("@docType", DocumentTypes.Incoming)
                    .WithParameter("@status", EnvelopeStatus.Scheduled)
                    .WithParameter("@envelopeId", messageId);

                using var iterator = _container.GetItemQueryIterator<IncomingMessage>(findQuery);
                while (iterator.HasMoreResults)
                {
                    var response = await iterator.ReadNextAsync(token);
                    foreach (var msg in response)
                    {
                        try
                        {
                            await _container.DeleteItemAsync<IncomingMessage>(msg.Id,
                                new PartitionKey(msg.PartitionKey), cancellationToken: token);
                        }
                        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Already deleted
                        }
                    }
                }
            }

            return;
        }

        // Query-based cancel
        var conditions = new List<string> { "c.docType = @docType", "c.status = @status" };
        if (query.MessageType.IsNotEmpty())
        {
            conditions.Add("c.messageType = @messageType");
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            conditions.Add("c.executionTime >= @execFrom");
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            conditions.Add("c.executionTime <= @execTo");
        }

        var queryText = $"SELECT * FROM c WHERE {string.Join(" AND ", conditions)}";
        var deleteQuery = BuildScheduledQuery(queryText, query);

        using var deleteIterator = _container.GetItemQueryIterator<IncomingMessage>(deleteQuery);
        while (deleteIterator.HasMoreResults)
        {
            var response = await deleteIterator.ReadNextAsync(token);
            foreach (var msg in response)
            {
                try
                {
                    await _container.DeleteItemAsync<IncomingMessage>(msg.Id,
                        new PartitionKey(msg.PartitionKey), cancellationToken: token);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    // Already deleted
                }
            }
        }
    }

    async Task IScheduledMessages.RescheduleAsync(Guid envelopeId, DateTimeOffset newExecutionTime,
        CancellationToken token)
    {
        var findQuery = new QueryDefinition(
                "SELECT * FROM c WHERE c.docType = @docType AND c.status = @status AND c.envelopeId = @envelopeId")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Scheduled)
            .WithParameter("@envelopeId", envelopeId);

        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(findQuery);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            foreach (var msg in response)
            {
                msg.ExecutionTime = newExecutionTime.ToUniversalTime();
                await _container.ReplaceItemAsync(msg, msg.Id, new PartitionKey(msg.PartitionKey),
                    cancellationToken: token);
            }
        }
    }

    async Task<IReadOnlyList<ScheduledMessageCount>> IScheduledMessages.SummarizeAsync(string serviceName,
        CancellationToken token)
    {
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.status = @status";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Scheduled);

        var messages = new List<IncomingMessage>();
        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(token);
            messages.AddRange(response);
        }

        return messages
            .GroupBy(x => x.MessageType)
            .Select(g => new ScheduledMessageCount(serviceName, g.Key, Uri, g.Count()))
            .ToList();
    }

    private QueryDefinition BuildScheduledQuery(string queryText, ScheduledMessageQuery query)
    {
        var qd = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Scheduled);

        if (query.MessageIds.Length > 0)
        {
            qd = qd.WithParameter("@messageIds", query.MessageIds);
        }

        if (query.MessageType.IsNotEmpty())
        {
            qd = qd.WithParameter("@messageType", query.MessageType);
        }

        if (query.ExecutionTimeFrom.HasValue)
        {
            qd = qd.WithParameter("@execFrom", query.ExecutionTimeFrom.Value);
        }

        if (query.ExecutionTimeTo.HasValue)
        {
            qd = qd.WithParameter("@execTo", query.ExecutionTimeTo.Value);
        }

        return qd;
    }
}
