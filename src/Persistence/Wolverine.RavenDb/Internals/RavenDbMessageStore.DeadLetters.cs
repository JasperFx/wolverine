using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime.Serialization;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IDeadLetters
{
    private static string dlqId(Guid id)
    {
        return $"dlq/{id}";
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        using var session = _store.OpenAsyncSession();
        var message = await session.LoadAsync<DeadLetterMessage>(dlqId(id));
        if (message is null) return null;

        return message.ToEnvelope();
    }

    public async Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var queryable = session.Query<DeadLetterMessage>().Customize(x => x.WaitForNonStaleResults());

        if (range.From.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.SentAt >= range.From.Value);
        }

        if (range.To.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.SentAt <= range.To.Value);
        }

        var messages = await queryable.ToListAsync(token);

        // Group by ReceivedAt, MessageType, ExceptionType
        var grouped = messages
            .GroupBy(x => new { ReceivedAt = x.ReceivedAt?.ToString() ?? "", x.MessageType, x.ExceptionType })
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
        // If MessageIds are specified, they take precedence
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            var envelopes = await loadByIdsAsync(query, token);

            return new DeadLetterEnvelopeResults
            {
                PageNumber = 1,
                TotalCount = envelopes.Count,
                Envelopes = envelopes,
                DatabaseUri = Uri
            };
        }

        if (query.PageNumber <= 0) query.PageNumber = 1;
        var skip = (query.PageNumber - 1) * query.PageSize;

        using var session = _store.OpenAsyncSession();
        var queryable = buildDeadLetterQuery(session, query);

        if (query.HasHeaderFilter())
        {
            // GH-5164: headers only exist inside the serialized envelope body, so the header criteria
            // are applied in memory and paging / the total count are computed over the header matches
            var matches = await findHeaderMatchesAsync(queryable, query, token);

            return new DeadLetterEnvelopeResults
            {
                PageNumber = query.PageNumber,
                TotalCount = matches.Count,
                Envelopes = query.PageSize > 0 ? matches.Skip(skip).Take(query.PageSize).ToList() : matches,
                DatabaseUri = Uri
            };
        }

        // Get total count
        var totalCount = await queryable.CountAsync(token);

        // A PageSize of 0 means "no paging", as it does for the relational stores
        var ordered = queryable.OrderBy(x => x.SentAt);
        var pagedMessages = query.PageSize > 0
            ? await ordered.Skip(skip).Take(query.PageSize).ToListAsync(token)
            : await ordered.ToListAsync(token);

        return new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            Envelopes = pagedMessages.Select(m => m.ToEnvelope()).ToList(),
            DatabaseUri = Uri
        };
    }

    private async Task<List<DeadLetterEnvelope>> loadByIdsAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var ids = query.MessageIds.Select(dlqId).ToArray();
        var messages = await session.LoadAsync<DeadLetterMessage>(ids, token);
        return messages.Values
            .Where(m => m != null)
            .Select(m => m!.ToEnvelope())
            .Where(e => query.MatchesHeaders(e.Envelope))
            .ToList();
    }

    private static async Task<List<DeadLetterEnvelope>> findHeaderMatchesAsync(
        IRavenQueryable<DeadLetterMessage> queryable, DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var messages = await queryable.OrderBy(x => x.SentAt).ThenBy(x => x.EnvelopeId).ToListAsync(token);
        return messages
            .Select(m => m.ToEnvelope())
            .Where(e => query.MatchesHeaders(e.Envelope))
            .ToList();
    }

    private static IRavenQueryable<DeadLetterMessage> buildDeadLetterQuery(IAsyncDocumentSession session,
        DeadLetterEnvelopeQuery query)
    {
        var queryable = session.Query<DeadLetterMessage>().Customize(x => x.WaitForNonStaleResults());

        if (query.Range.From.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.SentAt >= query.Range.From.Value);
        }

        if (query.Range.To.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.SentAt <= query.Range.To.Value);
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ExceptionType == query.ExceptionType);
        }

        if (query.ExceptionMessage.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ExceptionMessage.StartsWith(query.ExceptionMessage));
        }

        if (query.MessageType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.MessageType == query.MessageType);
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            var receivedAtUri = new Uri(query.ReceivedAt);
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ReceivedAt == receivedAtUri);
        }

        if (query.Replayable.HasValue)
        {
            var replayable = query.Replayable.Value;
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.Replayable == replayable);
        }

        return queryable;
    }

    // The RQL used by the bulk delete / patch operations below cannot express a header filter (headers
    // live in the serialized body), and BuildWhereClause has never expressed ExceptionMessage either.
    // Either criteria is resolved to the matching document ids first, then acted on by id, rather than
    // silently widening "discard / replay what matches" into a broader set.
    private static bool needsClientSideSelection(DeadLetterEnvelopeQuery query)
    {
        return query.HasHeaderFilter() ||
               ((query.MessageIds == null || query.MessageIds.Length == 0) && query.ExceptionMessage.IsNotEmpty());
    }

    private async Task<Guid[]> findMatchingIdsAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            return (await loadByIdsAsync(query, token)).Select(x => x.Id).ToArray();
        }

        using var session = _store.OpenAsyncSession();
        var matches = await findHeaderMatchesAsync(buildDeadLetterQuery(session, query), query, token);
        return matches.Select(x => x.Id).ToArray();
    }

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (needsClientSideSelection(query))
        {
            var ids = await findMatchingIdsAsync(query, token);

            // Nothing matched. Do NOT fall through to the unfiltered delete below
            if (ids.Length == 0) return;

            query = new DeadLetterEnvelopeQuery { MessageIds = ids };
        }

        // If MessageIds are specified, delete those specific messages
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            using var session = _store.OpenAsyncSession();
            foreach (var id in query.MessageIds)
            {
                session.Delete(dlqId(id));
            }
            await session.SaveChangesAsync(token);
            return;
        }

        // Build delete query
        var rql = BuildDeleteQuery(query);

        var op = await _store.Operations.SendAsync(new DeleteByQueryOperation(new IndexQuery
        {
            Query = rql,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 30.Seconds()
        }), token: token);

        await op.WaitForCompletionAsync();
    }

    public async Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        if (needsClientSideSelection(query))
        {
            var ids = await findMatchingIdsAsync(query, token);

            // Nothing matched. Do NOT fall through to the unfiltered patch below
            if (ids.Length == 0) return;

            query = new DeadLetterEnvelopeQuery { MessageIds = ids };
        }

        // If MessageIds are specified, mark those specific messages
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            using var session = _store.OpenAsyncSession();
            foreach (var id in query.MessageIds)
            {
                session.Advanced.Patch<DeadLetterMessage, bool>(dlqId(id), x => x.Replayable, true);
            }
            await session.SaveChangesAsync(token);
            return;
        }

        // Build update query
        var whereClause = BuildWhereClause(query);
        var rql = $@"
from DeadLetterMessages as m
{whereClause}
update
{{
    m.Replayable = true
}}";

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = rql,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 30.Seconds()
        }), token: token);

        await op.WaitForCompletionAsync();
    }

    private string BuildDeleteQuery(DeadLetterEnvelopeQuery query)
    {
        var whereClause = BuildWhereClause(query);
        return $"from DeadLetterMessages as m {whereClause}";
    }

    private string BuildWhereClause(DeadLetterEnvelopeQuery query)
    {
        var conditions = new List<string>();

        if (query.Range.From.HasValue)
        {
            conditions.Add($"m.SentAt >= '{query.Range.From.Value:o}'");
        }

        if (query.Range.To.HasValue)
        {
            conditions.Add($"m.SentAt <= '{query.Range.To.Value:o}'");
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            conditions.Add($"m.ExceptionType = '{query.ExceptionType}'");
        }

        if (query.MessageType.IsNotEmpty())
        {
            conditions.Add($"m.MessageType = '{query.MessageType}'");
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            conditions.Add($"m.ReceivedAt = '{query.ReceivedAt}'");
        }

        if (query.Replayable.HasValue)
        {
            conditions.Add($"m.Replayable = {(query.Replayable.Value ? "true" : "false")}");
        }

        return conditions.Count > 0 ? "where " + string.Join(" and ", conditions) : "";
    }

    // Legacy methods for backwards compatibility
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

    public async Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var message = await session.LoadAsync<DeadLetterMessage>(dlqId(envelopeId), token);
        if (message is null) return;

        // Deserialize the stored envelope, replace its Data with the new message body, re-serialize
        var envelope = EnvelopeSerializer.Deserialize(message.Body);
        envelope.Data = newBody;
        message.Body = EnvelopeSerializer.Serialize(envelope);
        message.Replayable = true;
        await session.SaveChangesAsync(token);
    }
}
