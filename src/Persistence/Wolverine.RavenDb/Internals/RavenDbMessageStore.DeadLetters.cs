using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;

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
        using var session = _store.OpenAsyncSession();
        var queryable = session.Query<DeadLetterMessage>().Customize(x => x.WaitForNonStaleResults());

        // If MessageIds are specified, they take precedence
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            var ids = query.MessageIds.Select(dlqId).ToArray();
            var messages = await session.LoadAsync<DeadLetterMessage>(ids, token);
            var envelopes = messages.Values
                .Where(m => m != null)
                .Select(m => m!.ToEnvelope())
                .ToList();

            return new DeadLetterEnvelopeResults
            {
                PageNumber = 1,
                TotalCount = envelopes.Count,
                Envelopes = envelopes,
                DatabaseUri = Uri
            };
        }

        // Apply filters
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

        // Get total count
        var totalCount = await queryable.CountAsync(token);

        // Apply paging
        if (query.PageNumber <= 0) query.PageNumber = 1;
        var skip = (query.PageNumber - 1) * query.PageSize;

        var pagedMessages = await queryable
            .OrderBy(x => x.SentAt)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(token);

        return new DeadLetterEnvelopeResults
        {
            PageNumber = query.PageNumber,
            TotalCount = totalCount,
            Envelopes = pagedMessages.Select(m => m.ToEnvelope()).ToList(),
            DatabaseUri = Uri
        };
    }

    public async Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
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
}
