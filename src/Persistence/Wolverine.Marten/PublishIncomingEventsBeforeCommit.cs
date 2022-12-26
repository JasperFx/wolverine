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
        // TODO -- SMELLY. Add something in Marten itself for this
        var events = session.PendingChanges.As<IChangeSet>().GetEvents().Select(x => x.Data).ToArray();

        if (events.Any())
        {
            foreach (var e in events) await _bus.PublishAsync(e);
        }
    }
}