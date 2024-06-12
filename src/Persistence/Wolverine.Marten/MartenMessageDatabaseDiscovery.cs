using Weasel.Core.Migrations;
using Wolverine.Postgresql;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

internal class MartenMessageDatabaseDiscovery : IDatabaseSource
{
    private readonly IWolverineRuntime _runtime;

    public MartenMessageDatabaseDiscovery(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        var list = new List<IDatabase>();

        if (_runtime.Storage is PostgresqlMessageStore database)
        {
            list.Add(database);
        }
        else if (_runtime.Storage is MultiTenantedMessageDatabase tenants)
        {
            await tenants.InitializeAsync(_runtime);
            list.AddRange(tenants.AllDatabases());
        }

        foreach (var ancillaryStore in _runtime.AncillaryStores)
        {
            if (ancillaryStore is PostgresqlMessageStore db)
            {
                list.Add(db);
            }
            else if (ancillaryStore is MultiTenantedMessageDatabase tenants)
            {
                await tenants.InitializeAsync(_runtime);
                list.AddRange(tenants.AllDatabases());
            }
        }

        return list;
    }
}