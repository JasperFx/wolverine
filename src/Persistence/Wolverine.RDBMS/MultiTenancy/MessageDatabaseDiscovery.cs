using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.MultiTenancy;

public class MessageDatabaseDiscovery : IDatabaseSource
{
    private readonly IWolverineRuntime _runtime;

    public MessageDatabaseDiscovery(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        var list = new List<IMessageDatabase>();

        if (_runtime.Storage is IMessageDatabase database)
        {
            list.Add(database);
        }
        else if (_runtime.Storage is MultiTenantedMessageStore tenants)
        {
            await tenants.InitializeAsync(_runtime);
            list.AddRange(tenants.ActiveDatabases().OfType<IMessageDatabase>());
        }

        foreach (var ancillaryStore in _runtime.AncillaryStores)
        {
            if (ancillaryStore is IMessageDatabase db)
            {
                list.Add(db);
            }
            else if (ancillaryStore is MultiTenantedMessageStore tenants)
            {
                await tenants.InitializeAsync(_runtime);
                list.AddRange(tenants.ActiveDatabases().OfType<IMessageDatabase>());
            }
        }

        var groups = list.GroupBy(x => x.Uri);
        return groups.Select(group =>
        {
            // It's important to use a "main" version because that has extra tables
            var master = group.FirstOrDefault(x => x.IsMain);
            return master ?? group.First();
        }).OfType<IDatabase>().ToList();
    }
}