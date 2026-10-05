using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.CosmosDb.Internals.Durability;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace CosmosDbTests;

/// <summary>
/// GH-4784. The agent's listener-discovery query was a bare, cross-partition <c>c.ownerId = 0</c> with no
/// status filter -- the issue mistakenly reported CosmosDB as having no such query. Once mark-as-handled
/// started releasing the owner, every <c>Handled</c> document retained for idempotency would nominate its
/// own listener for recovery, buying one page query that returns nothing, per listener, per polling cycle,
/// for the whole retention window. Goes through the real inbox API so it covers both halves of the fix.
/// </summary>
[Collection("cosmosdb")]
public class durability_recovery_listener_discovery : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;

    public durability_recovery_listener_discovery(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
                opts.ServiceName = "listener-discovery";
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_retained_handled_document_does_not_nominate_its_listener_for_recovery()
    {
        var store = _host.Services.GetRequiredService<IMessageStore>().As<CosmosDbMessageStore>();

        var pending = ObjectMother.Envelope();
        pending.Destination = new Uri("tcp://localhost:5801");
        pending.Status = EnvelopeStatus.Incoming;
        pending.OwnerId = TransportConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(pending);

        var handled = ObjectMother.Envelope();
        handled.Destination = new Uri("tcp://localhost:5802");
        handled.Status = EnvelopeStatus.Incoming;
        await store.Inbox.StoreIncomingAsync(handled);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(handled);

        var listeners = await findListenersWithRecoverableIncomingAsync(store);

        listeners.ShouldContain(pending.Destination);
        listeners.ShouldNotContain(handled.Destination);
    }

    private async Task<IReadOnlyList<Uri>> findListenersWithRecoverableIncomingAsync(CosmosDbMessageStore store)
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = (CosmosDbDurabilityAgent)store.BuildAgent(runtime);

        var method = typeof(CosmosDbDurabilityAgent).GetMethod(
            "findListenersWithRecoverableIncomingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        return await (Task<IReadOnlyList<Uri>>)method.Invoke(agent, null)!;
    }
}
