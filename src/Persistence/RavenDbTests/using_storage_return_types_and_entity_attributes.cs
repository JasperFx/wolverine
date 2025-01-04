using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;

namespace RavenDbTests;

[Collection("raven")]
public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    private readonly DatabaseFixture _fixture;

    public using_storage_return_types_and_entity_attributes(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void configureWolverine(WolverineOptions opts)
    {
        var store = _fixture.StartRavenStore();

        // You *must* register the store after the RavenDb envelope storage
        opts.UseRavenDbPersistence();
        opts.Services.AddSingleton(store);
        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;
        
        opts.CodeGeneration.ReferenceAssembly(typeof(Wolverine.RavenDb.IRavenDbOp).Assembly);
    }

    public override async Task<Todo?> Load(string id)
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();
        return await session.LoadAsync<Todo>(id);
    }

    public override async Task Persist(Todo todo)
    {
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(todo);
        await session.SaveChangesAsync();
    }
}