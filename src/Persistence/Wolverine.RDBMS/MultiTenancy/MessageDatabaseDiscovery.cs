using JasperFx.Descriptors;
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

    public async ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token)
    {
        var usage = new DatabaseUsage { Cardinality = Cardinality };
        
        if (_runtime.Storage is MultiTenantedMessageStore tenants)
        {
            await tenants.InitializeAsync(_runtime);

            usage.MainDatabase = tenants.Main.Describe();

            await tenants.Source.RefreshAsync();
            
            usage.Databases.AddRange(tenants.Source.AllActive().OfType<IMessageDatabase>().Select(x => x.Describe()));
        }
        else if (_runtime.Storage is IMessageDatabase md)
        {
            usage.MainDatabase = md.Describe();
            usage.Cardinality = DatabaseCardinality.Single;
        }
        else
        {
            usage.Cardinality = DatabaseCardinality.None;
        }

        return usage;
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

    public DatabaseCardinality Cardinality
    {
        get
        {
            if (_runtime.Storage is MultiTenantedMessageStore tenantedMessageStore)
            {
                return tenantedMessageStore.Source.Cardinality;
            }
            else
            {
                return DatabaseCardinality.Single;
            }
        }
    }
}