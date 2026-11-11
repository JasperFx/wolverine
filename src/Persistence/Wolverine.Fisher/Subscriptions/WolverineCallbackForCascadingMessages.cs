using JasperFx.Events.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.EventSourcing.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Fisher.Subscriptions;

// GH-3907: Fisher's subscriptions hand back JasperFx's IDaemonChangeListener rather than a
// store-specific IChangeListener. It is deliberately narrower - a post-batch signal with no
// change-set payload - so the "flush what the handler cascaded" half is all that is left.
/// <summary>
/// Flushes a subscription page's outgoing messages once the daemon commits, then disposes the page's
/// service scope if it has one. The behavior is the shared <see cref="SubscriptionMessageContext"/>.
/// </summary>
internal class WolverineCallbackForCascadingMessages : IDaemonChangeListener
{
    private readonly MessageContext _context;
    private readonly IServiceScope? _scope;

    public WolverineCallbackForCascadingMessages(MessageContext context, IServiceScope? scope = null)
    {
        _context = context;
        _scope = scope;
    }

    public Task AfterCommitAsync(CancellationToken token)
    {
        return SubscriptionMessageContext.AfterCommitAsync(_context, _scope);
    }
}
