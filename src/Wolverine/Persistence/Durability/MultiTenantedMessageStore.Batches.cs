namespace Wolverine.Persistence.Durability;

public partial class MultiTenantedMessageStore
{
    /// <summary>
    ///     GH-5962. Each envelope's rows belong in its tenant's database, so the batch is split by the resolved
    ///     database (many tenants can share one) and each database stores its share in its own transaction. A
    ///     database that already committed is not rolled back when a later one fails: atomic per database only.
    /// </summary>
    public async Task StoreEnvelopesAsync(IReadOnlyList<Envelope> incoming, IReadOnlyList<Envelope> outgoing,
        int outgoingOwnerId)
    {
        var groups = new Dictionary<IMessageStore, (List<Envelope> Incoming, List<Envelope> Outgoing)>();

        foreach (var envelope in incoming)
        {
            var database = await GetDatabaseAsync(envelope.TenantId);
            groupFor(groups, database).Incoming.Add(envelope);
        }

        foreach (var envelope in outgoing)
        {
            var database = await GetDatabaseAsync(envelope.TenantId);
            groupFor(groups, database).Outgoing.Add(envelope);
        }

        foreach (var (database, group) in groups)
        {
            await database.StoreEnvelopesAsync(group.Incoming, group.Outgoing, outgoingOwnerId);
        }
    }

    private static (List<Envelope> Incoming, List<Envelope> Outgoing) groupFor(
        Dictionary<IMessageStore, (List<Envelope> Incoming, List<Envelope> Outgoing)> groups, IMessageStore database)
    {
        if (!groups.TryGetValue(database, out var group))
        {
            group = ([], []);
            groups[database] = group;
        }

        return group;
    }
}
