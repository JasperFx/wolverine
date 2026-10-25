using JasperFx.Core;
using Raven.Client;
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
    private static readonly DateTimeOffset ArchivedAfter = DateTimeOffset.MinValue;

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
        // GH-5209: archived dead letters are left out of summaries
        var queryable = (IRavenQueryable<DeadLetterMessage>)session.Query<DeadLetterMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => !(x.ArchivedAt > ArchivedAfter));

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
        var queryable = buildDeadLetterQuery(session, query, query.EffectiveArchiveFilter());

        if (needsInMemoryQuery(query))
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

    private Task<List<DeadLetterEnvelope>> loadByIdsAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return loadByIdsAsync(query, query.EffectiveArchiveFilter(), token);
    }

    private async Task<List<DeadLetterEnvelope>> loadByIdsAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var ids = query.MessageIds.Select(dlqId).ToArray();
        var messages = await session.LoadAsync<DeadLetterMessage>(ids, token);
        var archiveQuery = new DeadLetterEnvelopeQuery { Archived = archived };
        return messages.Values
            .Where(m => m != null)
            .Select(m => m!.ToEnvelope())
            .Where(e => query.MatchesHeaders(e.Envelope) && archiveQuery.MatchesArchived(e.ArchivedAt))
            .ToList();
    }

    // Header criteria and a "contains" exception message match cannot be expressed in the query, so they are
    // applied in memory over everything else the query selects
    private static async Task<List<DeadLetterEnvelope>> findHeaderMatchesAsync(
        IRavenQueryable<DeadLetterMessage> queryable, DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var messages = await queryable.OrderBy(x => x.SentAt).ThenBy(x => x.EnvelopeId).ToListAsync(token);
        return messages
            .Select(m => m.ToEnvelope())
            .Where(e => query.MatchesHeaders(e.Envelope) && query.MatchesExceptionMessage(e.ExceptionMessage) &&
                        query.MatchesFailureGroup(e.FailureGroup, e.ExceptionType))
            .ToList();
    }

    private static IRavenQueryable<DeadLetterMessage> buildDeadLetterQuery(IAsyncDocumentSession session,
        DeadLetterEnvelopeQuery query, DeadLetterArchiveFilter archived)
    {
        var queryable = session.Query<DeadLetterMessage>().Customize(x => x.WaitForNonStaleResults());

        // GH-5209. A document stored before archiving existed has no ArchivedAt field at all, and RavenDb does not
        // match a missing field with "= null". A range predicate is false for both null and missing, so "archived"
        // is "after the minimum date" and its negation covers everything else
        if (archived == DeadLetterArchiveFilter.Exclude)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => !(x.ArchivedAt > ArchivedAfter));
        }
        else if (archived == DeadLetterArchiveFilter.Only)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ArchivedAt > ArchivedAfter);
        }

        if (query.FailureGroup.IsNotEmpty())
        {
            if (DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out var legacyType))
            {
                // "FailureGroup == null" would miss documents stored before the field existed, so the missing
                // failure group is checked in memory (see needsClientSideSelection)
                queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ExceptionType == legacyType);
            }
            else
            {
                var failureGroup = query.FailureGroup;
                queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.FailureGroup == failureGroup);
            }
        }

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
            var message = query.ExceptionMessage;
            switch (query.ExceptionMessageMatch)
            {
                case DeadLetterMessageMatch.Exact:
                    queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ExceptionMessage == message);
                    break;
                case DeadLetterMessageMatch.Contains:
                    // applied in memory, see needsClientSideSelection
                    break;
                default:
                    queryable = (IRavenQueryable<DeadLetterMessage>)queryable.Where(x => x.ExceptionMessage.StartsWith(message));
                    break;
            }
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
    // The LINQ query expresses the original "starts with" and the exact exception message match, but not these
    private static bool needsInMemoryQuery(DeadLetterEnvelopeQuery query)
    {
        return query.HasHeaderFilter() ||
               (query.ExceptionMessage.IsNotEmpty() && query.ExceptionMessageMatch == DeadLetterMessageMatch.Contains) ||
               DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out _);
    }

    private static bool needsClientSideSelection(DeadLetterEnvelopeQuery query)
    {
        // Only an exact exception message match is expressed in RQL. The original "starts with" match and
        // "contains" are applied in memory, as the original match always was for the bulk operations
        return query.HasHeaderFilter() ||
               (query.ExceptionMessage.IsNotEmpty() && query.ExceptionMessageMatch != DeadLetterMessageMatch.Exact) ||
               DeadLetterFingerprint.TryParseUnfingerprintedGroup(query.FailureGroup, out _);
    }

    private async Task<Guid[]> findMatchingIdsAsync(DeadLetterEnvelopeQuery query, DeadLetterArchiveFilter archived,
        CancellationToken token)
    {
        if (query.MessageIds != null && query.MessageIds.Any())
        {
            return (await loadByIdsAsync(query, archived, token)).Select(x => x.Id).ToArray();
        }

        using var session = _store.OpenAsyncSession();
        var matches = await findHeaderMatchesAsync(buildDeadLetterQuery(session, query, archived), query, token);
        return matches.Select(x => x.Id).ToArray();
    }

    // GH-5209: RavenDb reports counts and supports archiving, but keeps no replay history
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

    public Task<DeadLetterOperationResult> DiscardMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, query.EffectiveArchiveFilter(),
            (session, id) => session.Delete(dlqId(id)),
            null, token);
    }

    // An archived dead letter has to be restored before it can be replayed
    public Task<DeadLetterOperationResult> ReplayMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude,
            (session, id) => session.Advanced.Patch<DeadLetterMessage, bool>(dlqId(id), x => x.Replayable, true),
            ("m.Replayable = true", new Parameters()), token);
    }

    public Task<DeadLetterOperationResult> ArchiveMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Exclude,
            (session, id) =>
            {
                session.Advanced.Patch<DeadLetterMessage, DateTimeOffset?>(dlqId(id), x => x.ArchivedAt, now);
                session.Advanced.Patch<DeadLetterMessage, bool>(dlqId(id), x => x.Replayable, false);
            },
            ("m.ArchivedAt = $archivedAt; m.Replayable = false", new Parameters { ["archivedAt"] = now }), token);
    }

    public Task<DeadLetterOperationResult> RestoreMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        return executeMatchingAsync(query, DeadLetterArchiveFilter.Only,
            (session, id) =>
                session.Advanced.Patch<DeadLetterMessage, DateTimeOffset?>(dlqId(id), x => x.ArchivedAt, null),
            ("m.ArchivedAt = null", new Parameters()), token);
    }

    // GH-5209. Criteria the RQL cannot express are resolved to the matching ids first and acted on by id, rather
    // than silently widening "discard / replay what matches" into a broader set. Everything else runs as one
    // set-based operation whose result reports how many documents it touched. A null patch means delete.
    private async Task<DeadLetterOperationResult> executeMatchingAsync(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived, Action<IAsyncDocumentSession, Guid> actById,
        (string Script, Parameters Parameters)? patch, CancellationToken token)
    {
        if (needsClientSideSelection(query) || (query.MessageIds != null && query.MessageIds.Any()))
        {
            var ids = await findMatchingIdsAsync(query, archived, token);

            // Nothing matched. Do NOT fall through to the unfiltered operation below
            if (ids.Length == 0) return new DeadLetterOperationResult(0);

            using var session = _store.OpenAsyncSession();
            foreach (var id in ids)
            {
                actById(session, id);
            }

            await session.SaveChangesAsync(token);
            return new DeadLetterOperationResult(ids.Length);
        }

        var (whereClause, parameters) = BuildWhereClause(query, archived);

        Operation op;
        if (patch == null)
        {
            op = await _store.Operations.SendAsync(new DeleteByQueryOperation(new IndexQuery
            {
                Query = $"from DeadLetterMessages as m {whereClause}",
                QueryParameters = parameters,
                WaitForNonStaleResults = true,
                WaitForNonStaleResultsTimeout = 30.Seconds()
            }), token: token);
        }
        else
        {
            foreach (var pair in patch.Value.Parameters) parameters[pair.Key] = pair.Value;

            op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
            {
                Query = $@"
from DeadLetterMessages as m
{whereClause}
update
{{
    {patch.Value.Script}
}}",
                QueryParameters = parameters,
                WaitForNonStaleResults = true,
                WaitForNonStaleResultsTimeout = 30.Seconds()
            }), token: token);
        }

        var result = await op.WaitForCompletionAsync<BulkOperationResult>();
        return new DeadLetterOperationResult((int)result.Total);
    }

    private static (string WhereClause, Parameters Parameters) BuildWhereClause(DeadLetterEnvelopeQuery query,
        DeadLetterArchiveFilter archived)
    {
        var conditions = new List<string>();
        var parameters = new Parameters();

        if (query.Range.From.HasValue)
        {
            conditions.Add("m.SentAt >= $from");
            parameters["from"] = query.Range.From.Value;
        }

        if (query.Range.To.HasValue)
        {
            conditions.Add("m.SentAt <= $to");
            parameters["to"] = query.Range.To.Value;
        }

        if (query.ExceptionType.IsNotEmpty())
        {
            conditions.Add("m.ExceptionType = $exceptionType");
            parameters["exceptionType"] = query.ExceptionType;
        }

        // Only the exact match reaches here, see needsClientSideSelection
        if (query.ExceptionMessage.IsNotEmpty())
        {
            conditions.Add("m.ExceptionMessage = $exceptionMessage");
            parameters["exceptionMessage"] = query.ExceptionMessage;
        }

        if (query.MessageType.IsNotEmpty())
        {
            conditions.Add("m.MessageType = $messageType");
            parameters["messageType"] = query.MessageType;
        }

        if (query.ReceivedAt.IsNotEmpty())
        {
            conditions.Add("m.ReceivedAt = $receivedAt");
            parameters["receivedAt"] = query.ReceivedAt;
        }

        if (query.Replayable.HasValue)
        {
            conditions.Add("m.Replayable = $replayable");
            parameters["replayable"] = query.Replayable.Value;
        }

        if (query.FailureGroup.IsNotEmpty())
        {
            // The failure group of a dead letter recorded before fingerprinting is resolved in memory, see
            // needsClientSideSelection, and never reaches here
            conditions.Add("m.FailureGroup = $failureGroup");
            parameters["failureGroup"] = query.FailureGroup;
        }

        // See buildDeadLetterQuery for why this is a range rather than a null check
        if (archived == DeadLetterArchiveFilter.Exclude)
        {
            conditions.Add("true and not m.ArchivedAt > $archivedAfter");
            parameters["archivedAfter"] = ArchivedAfter;
        }
        else if (archived == DeadLetterArchiveFilter.Only)
        {
            conditions.Add("m.ArchivedAt > $archivedAfter");
            parameters["archivedAfter"] = ArchivedAfter;
        }

        return (conditions.Count > 0 ? "where " + string.Join(" and ", conditions) : "", parameters);
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
