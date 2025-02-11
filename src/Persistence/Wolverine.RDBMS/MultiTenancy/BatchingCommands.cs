using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.MultiTenancy;

internal interface IEnvelopeCommand
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

internal class StoreIncomingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageStore _store;
    private readonly Envelope[] _envelopes;

    public StoreIncomingAsyncGroup(IMessageStore store, Envelope[] envelopes)
    {
        _store = store;
        _envelopes = envelopes;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _store.Inbox.StoreIncomingAsync(_envelopes);
    }
}

internal class DeleteOutgoingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageStore _store;
    private readonly Envelope[] _envelopes;

    public DeleteOutgoingAsyncGroup(IMessageStore store, Envelope[] envelopes)
    {
        _store = store;
        _envelopes = envelopes;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _store.Outbox.DeleteOutgoingAsync(_envelopes);
    }
}

internal class DiscardAndReassignOutgoingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageStore _store;
    private readonly List<Envelope> _discards = new();
    private readonly int _nodeId;
    private readonly List<Envelope> _reassigned = new();

    public DiscardAndReassignOutgoingAsyncGroup(IMessageStore store, int nodeId)
    {
        _store = store;
        _nodeId = nodeId;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _store.Outbox.DiscardAndReassignOutgoingAsync(_discards.ToArray(), _reassigned.ToArray(), _nodeId);
    }

    public void AddDiscards(IEnumerable<Envelope> discards)
    {
        _discards.AddRange(discards);
    }

    public void AddReassigns(IEnumerable<Envelope> reassigns)
    {
        _reassigned.AddRange(reassigns);
    }
}