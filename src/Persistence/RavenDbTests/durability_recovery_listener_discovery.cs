using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.RavenDb.Internals.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace RavenDbTests;

/// <summary>
/// GH-4785. The agent's listener-discovery query used to be a bare <c>OwnerId == 0</c> with no status
/// filter, so once mark-as-handled started releasing the owner, every <c>Handled</c> document retained
/// for idempotency would nominate its own listener for recovery -- buying one page query that returns
/// nothing, per listener, per polling cycle, for the whole retention window.
/// </summary>
[Collection("raven")]
public class durability_recovery_listener_discovery : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public durability_recovery_listener_discovery(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _store = _fixture.StartRavenStore();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "listener-discovery";
                opts.UseRavenDbPersistence();
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
        var pending = new Uri("tcp://localhost:5801");
        var retained = new Uri("tcp://localhost:5802");

        using (var session = _store.OpenAsyncSession())
        {
            await session.StoreAsync(new IncomingMessage
            {
                Id = "IncomingMessages/pending",
                EnvelopeId = Guid.NewGuid(),
                ReceivedAt = pending,
                OwnerId = TransportConstants.AnyNode,
                Status = EnvelopeStatus.Incoming,
                MessageType = "pending"
            }, TestContext.Current.CancellationToken);

            // Exactly what MarkIncomingEnvelopeAsHandledAsync now leaves behind: Handled, no owner,
            // kept only until @expires removes it.
            await session.StoreAsync(new IncomingMessage
            {
                Id = "IncomingMessages/retained",
                EnvelopeId = Guid.NewGuid(),
                ReceivedAt = retained,
                OwnerId = TransportConstants.AnyNode,
                Status = EnvelopeStatus.Handled,
                MessageType = "retained"
            }, TestContext.Current.CancellationToken);

            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await waitForTheDiscoveryIndexAsync();

        var listeners = await findListenersWithRecoverableIncomingAsync();

        listeners.ShouldContain(pending);
        listeners.ShouldNotContain(retained);
    }

    /// <summary>
    /// The production query does not wait for non-stale results, so force the auto-index its shape needs
    /// to exist and be caught up first. Without this the assertion would be racing RavenDB's indexing.
    /// </summary>
    private async Task waitForTheDiscoveryIndexAsync()
    {
        using var session = _store.OpenAsyncSession();
        await session.Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(x => x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming)
            .Select(x => new { x.ReceivedAt })
            .Distinct()
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<Uri>> findListenersWithRecoverableIncomingAsync()
    {
        var store = _host.Services.GetRequiredService<IMessageStore>().As<RavenDbMessageStore>();
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
        var agent = (RavenDbDurabilityAgent)store.BuildAgent(runtime);

        var method = typeof(RavenDbDurabilityAgent).GetMethod(
            "findListenersWithRecoverableIncomingAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        return await (Task<IReadOnlyList<Uri>>)method.Invoke(agent, null)!;
    }
}
