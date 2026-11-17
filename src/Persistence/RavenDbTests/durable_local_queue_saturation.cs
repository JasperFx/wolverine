using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;

namespace RavenDbTests;

[Collection("raven")]
public class durable_local_queue_saturation : DurableLocalQueueSaturationCompliance
{
    private readonly DatabaseFixture _fixture;

    public durable_local_queue_saturation(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void configureWolverine(WolverineOptions opts)
    {
        // A new database per test, so nothing is left in the inbox from the last one
        var store = _fixture.StartRavenStore();

        opts.UseRavenDbPersistence();
        opts.Services.AddSingleton(store);
        opts.Policies.AutoApplyTransactions();
        opts.CodeGeneration.ReferenceAssembly(typeof(IRavenDbOp).Assembly);
    }

    public override async Task<Todo?> Load(string id)
    {
        using var session = Host.Services.GetRequiredService<IDocumentStore>().OpenAsyncSession();
        return await session.LoadAsync<Todo>(id);
    }
}
