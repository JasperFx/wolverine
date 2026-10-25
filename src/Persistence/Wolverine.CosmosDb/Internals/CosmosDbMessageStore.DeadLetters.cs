using System.Net;
using JasperFx.Core;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime.Serialization;

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
        // GH-5209: archived dead letters are left out of summaries
        var conditions = new List<string> { "c.docType = @docType", NotArchived };

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

            envelopes = envelopes.Where(e => query.MatchesHeaders(e.Envelope) && query.MatchesArchived(e.ArchivedAt)).ToList();

            return new DeadLetterEnvelopeResults
            {
                PageNumber = 1,
                TotalCount = envelopes.Count,
                Envelopes = envelopes,
                DatabaseUri = Uri
            };
        }

        if (query.HasHeaderFilter())
        {
            // GH-5164: headers only exist inside the serialized envelope body, so the header criteria
            // are applied in memory and paging / the total count are computed over the header matches
            if (query.PageNumber <= 0) query.PageNumber = 1;
            var matches = (await LoadDeadLettersByQuery(query, query.EffectiveArchiveFilter(), token))
                .OrderBy(x => x.SentAt)
                .ThenBy(x => x.EnvelopeId)
                .Select(x => x.ToEnvelope())
                .Where(x => query.MatchesHeaders(x.Envelope))
                .ToList();

            var offset = (query.PageNumber - 1) * query.PageSize;

            return new DeadLetterEnvelopeResults
            {
                PageNumber = query.PageNumber,
                TotalCount = matches.Count,
                Envelopes = query.PageSize > 0 ? matches.Skip(offset).Take(query.PageSize).ToList() : matches,
                DatabaseUri = Uri
            };
        }

        var conditions = buildConditions(query, query.EffectiveArchiveFilter());

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

    // GH-5209: CosmosDb reports counts and supports archiving, but keeps no replay history
    public DeadLetterCapabilities Capabilities => DeadLetterCapabilities.FailureGroups |
                                                  DeadLetterCapabilities.OperationCounts |
                                                  DeadLetterCapabilities.ArchiveAndRestore |
                                                  DeadLetterCapabilities.ExceptionMessageMatching;

    public Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return DiscardMatchingAsync(query, token);
    }

    public Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return ReplayMatchingAsync(query, token);
    }

    public async Task<DeadLetterOperationResult> DiscardMatchingAsync(DeadLetterEnvelopeQuery query,
        CancellationToken token)
    {
        var affected = 0;
        foreach (var message in await findMatchesAsync(query, query.EffectiveArchiveFilter(), token))
        {
            try
            {
                await _container.DeleteItemAsync<DeadLetterMessage>(message.Id,
                    new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
                affected++;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }

        return new DeadLetterOperationResult(affected);
    }

    // An archived dead letter has to be restored before it can be replayed
    public Task<DeadLetterOperationResult> ReplayMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return replaceMatchesAsync(query, DeadLetterArchiveFilter.Exclude, x => x.Replayable = true, token);
    }

    public Task<DeadLetterOperationResult> ArchiveMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return replaceMatchesAsync(query, DeadLetterArchiveFilter.Exclude, x =>
        {
            x.ArchivedAt = now;
            x.Replayable = false;
        }, token);
    }

    public Task<DeadLetterOperationResult> RestoreMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return replaceMatchesAsync(query, DeadLetterArchiveFilter.Only, x => x.ArchivedAt = null, token);
    }

    private async Task<DeadLetterOperationResult> replaceMatchesAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, Action<DeadLetterMessage> change, CancellationToken token)
    {
        var affected = 0;
        foreach (var message in await findMatchesAsync(query, archived, token))
        {
            change(message);
            try
            {
                await _container.ReplaceItemAsync(message, message.Id,
                    new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
                affected++;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }

        return new DeadLetterOperationResult(affected);
    }

    // Resolve a query to the documents it matches, honouring MessageIds and the header criteria
    private async Task<List<DeadLetterMessage>> findMatchesAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, CancellationToken token)
    {
        if (query.HasHeaderFilter())
        {
            return await findHeaderMatchesAsync(query, archived, token);
        }

        if (query.MessageIds != null && query.MessageIds.Any())
        {
            var archiveQuery = new DeadLetterEnvelopeQuery { Archived = archived };
            return (await readByIdsAsync(query.MessageIds, token))
                .Where(x => archiveQuery.MatchesArchived(x.ArchivedAt))
                .ToList();
        }

        return await LoadDeadLettersByQuery(query, archived, token);
    }

    private async Task<List<DeadLetterMessage>> readByIdsAsync(Guid[] ids, CancellationToken token)
    {
        var messages = new List<DeadLetterMessage>();
        foreach (var id in ids)
        {
            try
            {
                var response = await _container.ReadItemAsync<DeadLetterMessage>(DlqId(id),
                    new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
                messages.Add(response.Resource);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Skip missing
            }
        }

        return messages;
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

    // GH-5164: resolve a header-filtered query to the documents it matches, honouring MessageIds too
    private async Task<List<DeadLetterMessage>> findHeaderMatchesAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, CancellationToken token)
    {
        List<DeadLetterMessage> candidates;
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            var archiveQuery = new DeadLetterEnvelopeQuery { Archived = archived };
            candidates = (await readByIdsAsync(query.MessageIds, token))
                .Where(x => archiveQuery.MatchesArchived(x.ArchivedAt))
                .ToList();
        }
        else
        {
            candidates = await LoadDeadLettersByQuery(query, archived, token);
        }

        return candidates.Where(x => query.MatchesHeaders(x.ToEnvelope().Envelope)).ToList();
    }

    private async Task<List<DeadLetterMessage>> LoadDeadLettersByQuery(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, CancellationToken token)
    {
        var conditions = buildConditions(query, archived);

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

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        try
        {
            var response = await _container.ReadItemAsync<DeadLetterMessage>(DlqId(envelopeId),
                new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
            var message = response.Resource;

            // Deserialize the stored envelope, replace its Data with the new message body, re-serialize
            var envelope = EnvelopeSerializer.Deserialize(message.Body);
            envelope.Data = newBody;
            message.Body = EnvelopeSerializer.Serialize(envelope);
            message.Replayable = true;
            await _container.ReplaceItemAsync(message, message.Id,
                new PartitionKey(DocumentTypes.DeadLetterPartition), cancellationToken: token);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    private const string NotArchived = "(NOT IS_DEFINED(c.archivedAt) OR IS_NULL(c.archivedAt))";

    private static List<string> buildConditions(DeadLetterEnvelopeQuery query, DeadLetterArchiveFilter archived)
    {
        var conditions = new List<string> { "c.docType = @docType" };

        if (query.Range?.From.HasValue == true) conditions.Add("c.sentAt >= @from");
        if (query.Range?.To.HasValue == true) conditions.Add("c.sentAt <= @to");
        if (query.ExceptionType.IsNotEmpty()) conditions.Add("c.exceptionType = @exceptionType");

        if (query.ExceptionMessage.IsNotEmpty())
        {
            conditions.Add(query.ExceptionMessageMatch switch
            {
                DeadLetterMessageMatch.Exact => "c.exceptionMessage = @exceptionMessage",
                DeadLetterMessageMatch.Contains => "CONTAINS(c.exceptionMessage, @exceptionMessage)",
                _ => "STARTSWITH(c.exceptionMessage, @exceptionMessage)"
            });
        }

        if (query.MessageType.IsNotEmpty()) conditions.Add("c.messageType = @messageType");
        if (query.ReceivedAt.IsNotEmpty()) conditions.Add("c.receivedAt = @receivedAt");
        if (query.Replayable.HasValue) conditions.Add("c.replayable = @replayable");

        // GH-5209
        if (query.FailureGroup.IsNotEmpty())
        {
            conditions.Add(DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out _)
                ? "(NOT IS_DEFINED(c.failureGroup) OR IS_NULL(c.failureGroup)) AND c.exceptionType = @legacyExceptionType"
                : "c.failureGroup = @failureGroup");
        }

        if (archived == DeadLetterArchiveFilter.Exclude)
        {
            conditions.Add(NotArchived);
        }
        else if (archived == DeadLetterArchiveFilter.Only)
        {
            conditions.Add("(IS_DEFINED(c.archivedAt) AND NOT IS_NULL(c.archivedAt))");
        }

        return conditions;
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
        if (query.Replayable.HasValue) queryDef = queryDef.WithParameter("@replayable", query.Replayable.Value);

        if (query.FailureGroup.IsNotEmpty())
        {
            queryDef = DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out var legacyType)
                ? queryDef.WithParameter("@legacyExceptionType", legacyType)
                : queryDef.WithParameter("@failureGroup", query.FailureGroup);
        }

        return queryDef;
    }
}
