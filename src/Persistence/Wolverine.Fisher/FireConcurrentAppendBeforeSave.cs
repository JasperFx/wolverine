using Fisher;
using Fisher.Services;
using Wolverine.Tracking;

namespace Wolverine.Fisher;

/// <summary>
/// GH-4940. The store-wide listener behind <see cref="ConcurrentAppends" />: on an armed act's first save, before
/// it commits, it lands the concurrent append the act was armed with. A single volatile read per save otherwise.
/// </summary>
internal sealed class FireConcurrentAppendBeforeSave : IDocumentSessionListener
{
    public static readonly FireConcurrentAppendBeforeSave Instance = new();

    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        => ConcurrentAppends.FireIfArmedAsync(session.CorrelationId, token);

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        => Task.CompletedTask;
}
