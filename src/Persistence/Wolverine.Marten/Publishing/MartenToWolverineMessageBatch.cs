using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineMessageBatch(MessageContext Context, DocumentSessionBase Session) : IMessageBatch
{
    public async ValueTask PublishAsync<T>(T message)
    {
        await Context.PublishAsync(message);
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Context.FlushOutgoingMessagesAsync();
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}