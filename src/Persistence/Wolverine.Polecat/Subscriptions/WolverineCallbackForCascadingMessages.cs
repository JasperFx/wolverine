using Polecat;
using Polecat.Services;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.EventSourcing.Subscriptions;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Subscriptions;

/// <summary>
/// Flushes a subscription page's outgoing messages once the daemon commits, then disposes the page's
/// service scope if it has one. The behavior is the shared <see cref="SubscriptionMessageContext"/>.
/// </summary>
internal class WolverineCallbackForCascadingMessages : global::Polecat.IChangeListener
{
    private readonly MessageContext _context;
    private readonly IServiceScope? _scope;

    public WolverineCallbackForCascadingMessages(MessageContext context, IServiceScope? scope = null)
    {
        _context = context;
        _scope = scope;
    }

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return SubscriptionMessageContext.AfterCommitAsync(_context, _scope);
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
