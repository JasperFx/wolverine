using Fisher;
using Fisher.Services;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Fisher;

/// <summary>
/// GH-4940. The listener behind <see cref="ConcurrentAppends" />, added to a Wolverine session only while a tracked
/// session is active: on the first save of a session carrying an armed act's correlation id, before it commits, it
/// lands the concurrent append the act was armed with.
/// </summary>
internal sealed class FireConcurrentAppendBeforeSave(MessageContext context) : IDocumentSessionListener
{
    public Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        => ConcurrentAppends.FireIfArmedAsync(context, token);

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        => Task.CompletedTask;
}
