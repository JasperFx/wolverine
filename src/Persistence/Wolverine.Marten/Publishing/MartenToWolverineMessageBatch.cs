using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineMessageBatch(MessageContext Context, DocumentSessionBase Session) : IMessageBatch, ITenantedMessageSink
{
    public ValueTask PublishAsync<T>(T message)
    {
        return Context.PublishAsync(message);
    }

    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return Context.PublishAsync(message, new DeliveryOptions { TenantId = tenantId });
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