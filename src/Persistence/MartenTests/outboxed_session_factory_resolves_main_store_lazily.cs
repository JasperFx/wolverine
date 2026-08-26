using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace MartenTests;

/// <summary>
/// GH-4130. <c>OutboxedSessionFactory</c> used to capture <c>MessageStore = runtime.Storage</c> in its
/// constructor. <c>IWolverineRuntime.Storage</c> is <c>Stores.Main</c>, which is the placeholder
/// <see cref="NullMessageStore"/> until <c>MessageStoreCollection.InitializeAsync()</c> assigns the real
/// one — and that assignment is deferred whenever more than one store claims
/// <see cref="MessageStoreRole.Main"/> and <c>ResolveMainStoreOnConflict</c> (GH-3226) has to reconcile
/// them.
/// </summary>
/// <remarks>
/// <para>
/// The shape that hits it is ordinary: an event-store-integrated Main plus a database-backed queue
/// transport, which also claims Main. The factory kept the placeholder for the life of the process while
/// <c>Stores.Main</c> read perfectly correct afterwards — so the host booted and listened cleanly, then
/// failed every message and HTTP request with "This Wolverine application is not using Postgresql +
/// Marten as the backing message persistence" (Polecat's twin says "requires a SQL Server-backed message
/// store … was NullMessageStore").
/// </para>
/// <para>
/// ⚠️ <b>Assert by opening a session, not by inspecting store roles.</b> The roles are correct — the
/// reconciler does exactly what it is supposed to. A test that checks <c>Stores.Main</c>, or counts Main
/// stores, passes on a host that fails 100% of its work.
/// </para>
/// </remarks>
public class outboxed_session_factory_resolves_main_store_lazily : PostgresqlContext
{
    [Fact]
    public async Task opens_a_session_when_main_was_settled_by_reconciliation()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Registered BEFORE Wolverine's own hosted service, so it runs before
                // MessageStoreCollection.InitializeAsync(). This is not contrived: an integration that
                // wires hosted services through opts.Services (CritterWatch does, and so does anything
                // provisioning resources at startup) lands them ahead of Wolverine's in the collection.
                services.AddHostedService<ResolvesTheSessionFactoryAtStartup>();
            })
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                // Claimant one: the Postgres queue transport's own persistence, on its own schema.
                opts.UsePostgresqlPersistenceAndTransport(
                        Servers.PostgresConnectionString, "lazymain_q", "lazymain_q_queues")
                    .AutoProvision();

                // Claimant two: Marten's integrated store, on another.
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "lazymain";
                    })
                    .IntegrateWithWolverine(w => w.MessageStorageSchemaName = "lazymain_wolverine");

                // Two Mains, so Wolverine defers the Main assignment to InitializeAsync and reconciles
                // there. Without a resolver it simply throws, and the deferral never happens.
                opts.Durability.ResolveMainStoreOnConflict = mains =>
                    mains.FirstOrDefault(s => s.Uri.AbsolutePath.EndsWith("lazymain_q"));
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        // Reconciliation worked — this was never the broken part.
        runtime.Stores.Main.ShouldNotBeOfType<NullMessageStore>();

        // ...and the factory sees the same store, rather than the placeholder it was built alongside.
        var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();
        using var session = factory.OpenSession(new MessageContext(runtime));

        session.ShouldNotBeNull();
    }

    /// <summary>
    /// Forces the singleton <see cref="OutboxedSessionFactory"/> to be constructed while
    /// <c>Stores.Main</c> is still the placeholder. Without this the factory is first resolved after
    /// startup, when <c>runtime.Storage</c> already reads correctly — which is why the defect looked
    /// intermittent and why store-role assertions never saw it.
    /// </summary>
    private sealed class ResolvesTheSessionFactoryAtStartup(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            services.GetRequiredService<OutboxedSessionFactory>();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
