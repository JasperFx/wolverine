using Marten;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten;

internal class FlushOutgoingMessagesOnCommit : DocumentSessionListenerBase
{
    private readonly MessageContext _context;

    public FlushOutgoingMessagesOnCommit(MessageContext context)
    {
        _context = context;
    }

    public override void AfterCommit(IDocumentSession session, IChangeSet commit)
    {
#pragma warning disable VSTHRD002
        _context.FlushOutgoingMessagesAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return _context.FlushOutgoingMessagesAsync();
    }
}