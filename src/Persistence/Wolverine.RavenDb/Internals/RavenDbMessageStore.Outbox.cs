using Raven.Client.Documents;
using Wolverine.Persistence.Durability;

namespace Wolverine.RavenDb.Internals;

public partial class RavenDbMessageStore : IMessageOutbox
{
    // Only called from DurabilityAgent stuff
    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        // TODO -- need an index for destination
        using var session = _store.OpenAsyncSession();
        var outgoing = await session
            .Query<OutgoingMessage>()
            .Where(x => x.Destination == destination)
            .ToListAsync();

        return outgoing.Select(x => x.Read()).ToList();
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        using var session = _store.OpenAsyncSession();
        var outgoing = new OutgoingMessage(envelope)
        {
            OwnerId = ownerId
        };
        
        await session.StoreAsync(outgoing);
        await session.SaveChangesAsync();
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var envelope in envelopes)
        {
            session.Delete(envelope.Id.ToString());
        }
        
        await session.SaveChangesAsync();
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        using var session = _store.OpenAsyncSession();
        session.Delete(envelope.Id.ToString());
        await session.SaveChangesAsync();
    }

    // Only called from DurabilityAgent
    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        using var session = _store.OpenAsyncSession();
        foreach (var discard in discards)
        {
            session.Delete(discard.Id.ToString());
        }

        foreach (var envelope in reassigned)
        {
            session.Advanced.Patch<OutgoingMessage, int>(envelope.Id.ToString(), x => x.OwnerId, nodeId);
        }

        await session.SaveChangesAsync();
    }
}