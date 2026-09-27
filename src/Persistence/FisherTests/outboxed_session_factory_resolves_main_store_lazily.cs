using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;

namespace FisherTests;

/// <summary>
/// GH-4633, the Fisher arm of GH-4130. <c>OutboxedSessionFactory</c> captured
/// <c>MessageStore = runtime.Storage</c> in its constructor. <c>IWolverineRuntime.Storage</c> is
/// <c>Stores.Main</c>, which is the placeholder <see cref="NullMessageStore"/> until
/// <c>MessageStoreCollection.InitializeAsync()</c> assigns the real one — and that assignment is
/// deferred whenever more than one store claims <see cref="MessageStoreRole.Main"/> and
/// <c>ResolveMainStoreOnConflict</c> (GH-3226) has to reconcile them.
/// </summary>
/// <remarks>
/// <para>
/// Fisher can reach that window: a SQLite file is a database, so
/// <c>UseSqlitePersistenceAndTransport</c> against one file plus an integrated Fisher store on
/// another is two Main claimants and two distinct store Uris, exactly the "event-store-integrated
/// Main plus a database-backed queue transport" shape that broke Marten and Polecat. Two files
/// rather than one, because a Fisher store IS its SQLite file and two pooled data sources against
/// one file is the second writer Fisher's docs say not to create.
/// </para>
/// <para>
/// ⚠️ <b>Assert by opening a session, not by inspecting store roles.</b> The roles are correct — the
/// reconciler does exactly what it is supposed to. A test that checks <c>Stores.Main</c>, or counts
/// Main stores, passes on a host that fails 100% of its work: against the constructor capture this
/// factory holds the placeholder forever and <c>resolveSqliteMessageStore()</c> throws
/// "Wolverine.Fisher requires a SQLite-backed message store … was NullMessageStore" on every message
/// and HTTP request.
/// </para>
/// </remarks>
public class outboxed_session_factory_resolves_main_store_lazily : IAsyncLifetime
{
    private FisherTestDatabase theFisherDatabase = null!;
    private FisherTestDatabase theQueueDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theFisherDatabase = Servers.CreateDatabase("lazymain_fisher");
        theQueueDatabase = Servers.CreateDatabase("lazymain_queues");

        theHost = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                // Registered BEFORE Wolverine's own hosted service, so it runs before
                // MessageStoreCollection.InitializeAsync(). Not contrived: an integration that wires
                // hosted services through opts.Services lands them ahead of Wolverine's.
                services.AddHostedService<ResolvesTheSessionFactoryAtStartup>();
            })
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                // Claimant one: the SQLite queue transport's own persistence, in its own file.
                opts.UseSqlitePersistenceAndTransport(theQueueDatabase.ConnectionString)
                    .AutoProvision();

                // Claimant two: Fisher's integrated store, in another.
                opts.Services.AddFisher(o =>
                    {
                        o.Connection(theFisherDatabase.ConnectionString);
                        o.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(w => w.MessageStorageSchemaName = "lazymain_wolverine");

                // Two Mains, so Wolverine defers the Main assignment to InitializeAsync and
                // reconciles there. Without a resolver it simply throws, and the deferral never
                // happens.
                opts.Durability.ResolveMainStoreOnConflict = mains =>
                    mains.FirstOrDefault(s => s.Uri.AbsolutePath.EndsWith("lazymain_wolverine"));
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theFisherDatabase.Dispose();
        theQueueDatabase.Dispose();
    }

    [Fact]
    public async Task opens_a_session_when_main_was_settled_by_reconciliation()
    {
        var runtime = theHost.Services.GetRequiredService<IWolverineRuntime>();

        // Reconciliation worked — this was never the broken part.
        runtime.Stores.Main.ShouldNotBeOfType<NullMessageStore>();

        // ...and the factory sees the same store, rather than the placeholder it was built alongside.
        var factory = theHost.Services.GetRequiredService<OutboxedSessionFactory>();
        await using var session = factory.OpenSession(new MessageContext(runtime));

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
