using Wolverine.Persistence.Durability;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5278. A <see cref="NullMessageStore" /> whose inbox writes fail while <see cref="InboxIsDown" /> is set, so a
/// durable listener can be shown what to do when it cannot store what it received -- without a database to take
/// down. Everything else, outbox included, behaves as the null store always does. Re-listing
/// <see cref="IMessageInbox" /> re-maps only its two store methods to the ones below.
/// </summary>
public class InboxOutageMessageStore : NullMessageStore, IMessageInbox
{
    private int _failedWrites;

    public bool InboxIsDown { get; set; }

    /// <summary>
    /// How many inbox writes have failed so far, to know an outage was actually hit
    /// </summary>
    public int FailedWrites => _failedWrites;

    public new Task StoreIncomingAsync(Envelope envelope)
    {
        return InboxIsDown ? fail() : base.StoreIncomingAsync(envelope);
    }

    public new Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        return InboxIsDown ? fail() : base.StoreIncomingAsync(envelopes);
    }

    private Task fail()
    {
        Interlocked.Increment(ref _failedWrites);
        return Task.FromException(new TimeoutException("The inbox is unreachable"));
    }
}
