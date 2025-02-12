using Weasel.Core.Migrations;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Marten;

// The purpose of this type is to expose the PostgreSQL message stores created
// because of Marten integration with the JasperFx
internal class MartenMessageDatabaseDiscovery : IDatabaseSource
{
    private readonly IWolverineRuntime _runtime;

    public MartenMessageDatabaseDiscovery(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        var list = new List<PostgresqlMessageStore>();

        if (_runtime.Storage is PostgresqlMessageStore store)
        {
            list.Add(store);
        }
        else if (_runtime.Storage is MultiTenantedMessageStore tenants)
        {
            await tenants.InitializeAsync(_runtime);
            list.AddRange(tenants.ActiveDatabases().OfType<PostgresqlMessageStore>());
        }

        foreach (var ancillaryStore in _runtime.AncillaryStores)
        {
            if (ancillaryStore is PostgresqlMessageStore db)
            {
                list.Add(db);
            }
            else if (ancillaryStore is MultiTenantedMessageStore tenants)
            {
                await tenants.InitializeAsync(_runtime);
                list.AddRange(tenants.ActiveDatabases().OfType<PostgresqlMessageStore>());
            }
        }

        var groups = list.GroupBy(x => x.Uri);
        return groups.Select(group =>
        {
            // It's important to use a "master" version because that has extra tables
            var master = group.FirstOrDefault(x => x.IsMaster);
            return master ?? group.First();
        }).ToList();
    }
}