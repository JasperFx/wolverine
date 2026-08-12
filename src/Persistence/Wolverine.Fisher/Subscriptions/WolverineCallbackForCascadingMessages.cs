using JasperFx.Events.Daemon;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Subscriptions;

// GH-3907: Fisher's subscriptions hand back JasperFx's IDaemonChangeListener rather than a
// store-specific IChangeListener. It is deliberately narrower - a post-batch signal with no
// change-set payload - so the "flush what the handler cascaded" half is all that is left.
internal class WolverineCallbackForCascadingMessages : IDaemonChangeListener
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
