using JasperFx.Descriptors;
using Weasel.Core.Migrations;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

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

        usage.Cardinality = _runtime.Stores.Cardinality();

        return usage;
    }

    public async ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        if (!_runtime.Options.ExternalTransportsAreStubbed)
        {
            foreach (var transport in _runtime.Options.Transports.OfType<ITransportConfiguresRuntime>().ToArray())
            {
                await transport.ConfigureAsync(_runtime);
            }
        }
        
        return await _runtime.Stores.FindAllAsync<IDatabase>();
    }

    public DatabaseCardinality Cardinality => _runtime.Stores.Cardinality();
}