namespace Wolverine.RDBMS.MultiTenancy;

internal interface IEnvelopeCommand
{
    Task ExecuteAsync(CancellationToken cancellationToken);
}

internal class StoreIncomingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageDatabase _database;
    private readonly Envelope[] _envelopes;

    public StoreIncomingAsyncGroup(IMessageDatabase database, Envelope[] envelopes)
    {
        _database = database;
        _envelopes = envelopes;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _database.Inbox.StoreIncomingAsync(_envelopes);
    }
}

internal class DeleteOutgoingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageDatabase _database;
    private readonly Envelope[] _envelopes;

    public DeleteOutgoingAsyncGroup(IMessageDatabase database, Envelope[] envelopes)
    {
        _database = database;
        _envelopes = envelopes;
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _database.Outbox.DeleteOutgoingAsync(_envelopes);
    }
}


internal class DiscardAndReassignOutgoingAsyncGroup : IEnvelopeCommand
{
    private readonly IMessageDatabase _database;
    private readonly List<Envelope> _discards = new();
    private readonly List<Envelope> _reassigned = new();
    private readonly int _nodeId;

    public DiscardAndReassignOutgoingAsyncGroup(IMessageDatabase database, int nodeId)
    {
        _database = database;
        _nodeId = nodeId;
    }

    public void AddDiscards(IEnumerable<Envelope> discards)
    {
        _discards.AddRange(discards);
    }

    public void AddReassigns(IEnumerable<Envelope> reassigns)
    {
        _reassigned.AddRange(reassigns);
    }

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return _database.Outbox.DiscardAndReassignOutgoingAsync(_discards.ToArray(), _reassigned.ToArray(), _nodeId);
    }
}

