using Polecat;

namespace Wolverine.Polecat;

internal class PublishIncomingEventsBeforeCommit : IDocumentSessionListener
{
    private readonly IMessageContext _bus;

    public PublishIncomingEventsBeforeCommit(IMessageContext bus)
    {
        _bus = bus;
    }

    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var events = session.PendingChanges.Streams.SelectMany(s => s.Events).ToArray();

        if (events.Length != 0)
        {
            foreach (var e in events)
            {
                await _bus.PublishAsync(e);
            }
        }
    }

    public Task AfterCommitAsync(IDocumentSession session, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
