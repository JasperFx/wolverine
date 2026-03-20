using Polecat.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Subscriptions;

internal class WolverineCallbackForCascadingMessages : global::Polecat.Subscriptions.IChangeListener
{
    private readonly MessageContext _context;

    public WolverineCallbackForCascadingMessages(MessageContext context)
    {
        _context = context;
    }

    public async Task AfterCommitAsync(CancellationToken token)
    {
        await _context.FlushOutgoingMessagesAsync();
    }
}
