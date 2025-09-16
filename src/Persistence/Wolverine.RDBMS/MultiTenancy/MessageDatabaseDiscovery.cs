using JasperFx.Descriptors;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.MultiTenancy;

public class MessageDatabaseDiscovery : IDatabaseSource
{
    private readonly WolverineRuntime _runtime;

    public MessageDatabaseDiscovery(IWolverineRuntime runtime)
    {
        // Yeah, I know, this is awful. But also working just fine
        _runtime = (WolverineRuntime)runtime;
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

    public ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        return _runtime.Stores.FindAllAsync<IDatabase>();
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