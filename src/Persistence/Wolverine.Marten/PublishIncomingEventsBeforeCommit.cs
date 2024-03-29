using JasperFx.Core.Reflection;
using Marten;
using Marten.Services;

namespace Wolverine.Marten;

internal class PublishIncomingEventsBeforeCommit : DocumentSessionListenerBase
{
    private readonly IMessageContext _bus;

    public PublishIncomingEventsBeforeCommit(IMessageContext bus)
    {
        _bus = bus;
    }

    public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var events = session.PendingChanges.As<IChangeSet>().GetEvents().ToArray();

        if (events.Any())
        {
            foreach (var e in events)
            {
                await _bus.PublishAsync(e);
            }
        }
    }
}

