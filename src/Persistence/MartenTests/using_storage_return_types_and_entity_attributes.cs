using IntegrationTests;
using Marten;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;

namespace MartenTests;

public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "todos";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();
        
        opts.Policies.AutoApplyTransactions();
    }

    public override async Task<Todo?> Load(string id)
    {
        var store = Host.DocumentStore();
        await using var session = store.QuerySession();
        return await session.LoadAsync<Todo>(id);
    }

    public override async Task Persist(Todo todo)
    {
        var store = Host.DocumentStore();
        await using var session = store.LightweightSession();
        session.Store(todo);
        await session.SaveChangesAsync();
    }
}

