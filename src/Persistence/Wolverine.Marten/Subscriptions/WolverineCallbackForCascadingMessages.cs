using Marten;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Subscriptions;

internal class WolverineCallbackForCascadingMessages : IChangeListener
{
    private readonly MessageContext _context;

    public WolverineCallbackForCascadingMessages(MessageContext context)
    {
        _context = context;
    }

    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        await _context.FlushOutgoingMessagesAsync();
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}