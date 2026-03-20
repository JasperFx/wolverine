using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Polecat;

namespace PolecatTests;

public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "todos";
        }).IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();
    }

    protected override async Task initialize()
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public override async Task<Todo?> Load(string id)
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        return await session.LoadAsync<Todo>(id);
    }

    public override async Task Persist(Todo todo)
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        session.Store(todo);
        await session.SaveChangesAsync();
    }
}
