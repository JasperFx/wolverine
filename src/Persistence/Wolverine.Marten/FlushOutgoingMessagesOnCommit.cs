using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Marten;
using Marten.Events;
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
            foreach (var e in events)
            {
                await _bus.PublishAsync(e);
            }
        }
    }
}

internal class FlushOutgoingMessagesOnCommit : DocumentSessionListenerBase
{
    private readonly IMessageContext _bus;

    public FlushOutgoingMessagesOnCommit(IMessageContext bus)
    {
        _bus = bus;
    }

    public override void AfterCommit(IDocumentSession session, IChangeSet commit)
    {
#pragma warning disable VSTHRD002
        _bus.FlushOutgoingMessagesAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _bus.FlushOutgoingMessagesAsync();
    }
}
