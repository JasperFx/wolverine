using JasperFx.Core;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageStoreAdmin
{
    public Task DeleteAllHandledAsync()
    {
        throw new NotSupportedException("This function is not yet supported by RavenDb");
    }

    public async Task ClearAllAsync()
    {
        await _store.DeleteAllAsync<IncomingMessage>();
        await _store.DeleteAllAsync<OutgoingMessage>();
        await _store.DeleteAllAsync<DeadLetterMessage>();
    }

    public Task RebuildAsync()
    {
        return ClearAllAsync();
    }

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        using var session = _store.OpenAsyncSession();
        return new PersistedCounts
        {
            DeadLetter = await session.Query<DeadLetterMessage>().CountAsync(),
            Handled =
                await session.Query<IncomingMessage>().Where(m => m.Status == EnvelopeStatus.Handled).CountAsync(),
            Incoming = await session.Query<IncomingMessage>().Where(m => m.Status == EnvelopeStatus.Incoming)
                .CountAsync(),
            Outgoing = await session.Query<OutgoingMessage>().CountAsync(),
            Scheduled = await session.Query<IncomingMessage>().Where(m => m.Status == EnvelopeStatus.Scheduled)
                .CountAsync(),
        };
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        using var session = _store.OpenAsyncSession();
        var messages = await session.Query<IncomingMessage>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        return messages.Select(m => m.Read()).ToList();
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        using var session = _store.OpenAsyncSession();
        var messages = await session.Query<OutgoingMessage>().Customize(x => x.WaitForNonStaleResults()).ToListAsync();
        return messages.Select(m => m.Read()).ToList();
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        using var session = _store.OpenAsyncSession();

        var query1 = new IndexQuery
        {
            Query = $@"
from IncomingMessages as m
update
{{
    m.OwnerId = 0
}}",
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds()
        };
        var op1 = await _store.Operations.SendAsync(new PatchByQueryOperation(query1));
        await op1.WaitForCompletionAsync();

        var query2 = new IndexQuery
        {
            Query = $@"
from OutgoingMessages as m
update
{{
    m.OwnerId = 0
}}",
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds()
        };

        var op2 = await _store.Operations.SendAsync(new PatchByQueryOperation(query2));
        await op2.WaitForCompletionAsync();
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        var command = $@"
from IncomingMessages as m
where m.OwnerId = {ownerId}
update
{{
    m.OwnerId = 0
}}";

        var op1 = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = command,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds()
        }));
        await op1.WaitForCompletionAsync();

        var command2 = $@"
from OutgoingMessages as m
where m.OwnerId = {ownerId}
update
{{
    m.OwnerId = 0
}}";

        var op2 = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = command2,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = 10.Seconds()
        }));
        await op2.WaitForCompletionAsync();
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        using var session = _store.OpenAsyncSession();
        var count = await session.Query<IncomingMessage>().CountAsync(token: token);
    }

    public Task MigrateAsync()
    {
        // TODO -- any work here for indexes?
        return Task.CompletedTask;
    }
}