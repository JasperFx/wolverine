using Fisher;
using Fisher.Services;

namespace Wolverine.Fisher;

internal class PublishIncomingEventsBeforeCommit : IDocumentSessionListener
{
    private readonly IMessageContext _bus;

    public PublishIncomingEventsBeforeCommit(IMessageContext bus)
    {
        _bus = bus;
    }

    public async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
    {
        var events = session.Events.PendingStreams.SelectMany(s => s.Events).ToArray();

        if (events.Length != 0)
        {
            foreach (var e in events)
            {
                await _bus.PublishAsync(e);
            }
        }
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
