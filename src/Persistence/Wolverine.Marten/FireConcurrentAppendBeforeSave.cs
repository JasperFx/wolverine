using Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Marten;

/// <summary>
/// GH-4940. The listener behind <see cref="ConcurrentAppends" />, added to a Wolverine session only while a tracked
/// session is active: on the first save of a session carrying an armed act's correlation id, before it commits, it
/// lands the concurrent append the act was armed with.
/// </summary>
internal sealed class FireConcurrentAppendBeforeSave(MessageContext context) : DocumentSessionListenerBase
{
    public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        => ConcurrentAppends.FireIfArmedAsync(context, token);
}
