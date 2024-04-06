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

    public ValueTask<IReadOnlyList<IDatabase>> BuildDatabases()
    {
        if (_runtime.Storage is PostgresqlMessageStore database)
            return new ValueTask<IReadOnlyList<IDatabase>>(new List<IDatabase>{database});

        if (_runtime.Storage is MultiTenantedMessageDatabase tenants)
        {
            tenants.Initialize(_runtime);
            return new ValueTask<IReadOnlyList<IDatabase>>(tenants.AllDatabases());
        }

        return new ValueTask<IReadOnlyList<IDatabase>>(Array.Empty<IDatabase>());
    }
}