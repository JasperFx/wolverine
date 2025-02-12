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
    
    public async Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId = null)
    {
        using var session = _store.OpenAsyncSession();
        var queryable = session.Query<DeadLetterMessage>().Customize(x => x.WaitForNonStaleResults());
        if (queryParameters.StartId.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.EnvelopeId >= queryParameters.StartId.Value);
        }
        
        if (queryParameters.MessageType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.MessageType == queryParameters.MessageType);
        }
        
        if (queryParameters.ExceptionType.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.ExceptionType == queryParameters.ExceptionType);
        }
        
        if (queryParameters.ExceptionMessage.IsNotEmpty())
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.ExceptionMessage == queryParameters.ExceptionMessage);
        }
        
        if (queryParameters.From.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.SentAt >= queryParameters.From.Value);
        }
        
        if (queryParameters.Until.HasValue)
        {
            queryable = (IRavenQueryable<DeadLetterMessage>)Queryable.Where(queryable, x => x.SentAt <= queryParameters.Until.Value);
        }
        
        var messages = await queryable
            .OrderBy(x => x.SentAt)
            .Take((int)queryParameters.Limit + 1)
            .ToListAsync();

        var envelopes = messages.Select(x => x.ToEnvelope()).ToList();

        var next = Guid.Empty;
        if (envelopes.Count > queryParameters.Limit)
        {
            next = envelopes.Last().Envelope.Id;
            envelopes.RemoveAt(envelopes.Count - 1);
        }
        
        return new DeadLetterEnvelopesFound(envelopes, next, tenantId);
    }

    public async Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null)
    {
        using var session = _store.OpenAsyncSession();
        var message = await session.LoadAsync<DeadLetterMessage>(dlqId(id));
        if (message is null) return null;
        
        return message.ToEnvelope();
    }

    public async Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "")
    {
        using var session = _store.OpenAsyncSession();
        var count = exceptionType.IsEmpty()
            ? await session.Query<DeadLetterMessage>().CountAsync()
            : await session.Query<DeadLetterMessage>().CountAsync(x => x.ExceptionType == exceptionType);

        string command = null;
        if (exceptionType.IsEmpty())
        {
            command = $@"
from DeadLetterMessages as m
update
{{
    m.Replayable = true
}}";
        }
        else
        {
            command = $@"
from DeadLetterMessages as m
where m.ExceptionType = @exceptionType
update
{{
    m.Replayable = true
}}";
        }

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = command,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds(),
            QueryParameters = new(){{"exceptionType", exceptionType}}
        }));
        await op.WaitForCompletionAsync();

        return count;
    }

    public async Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var id in ids)
        {
            session.Advanced.Patch<DeadLetterEnvelope, bool>(dlqId(id), x => x.Replayable, true);
        }
        
        await session.SaveChangesAsync();
    }

    public async Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var id in ids)
        {
            session.Delete(dlqId(id));
        }
        
        await session.SaveChangesAsync();
    }
}