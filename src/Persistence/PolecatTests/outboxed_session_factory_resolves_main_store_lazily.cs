using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Runtime;
using Wolverine.SqlServer;
using Xunit;

namespace PolecatTests;

/// <summary>
/// GH-4130. The Polecat twin of <c>MartenTests.outboxed_session_factory_resolves_main_store_lazily</c> — see that
/// test for the full rationale. <c>OutboxedSessionFactory</c> captured
/// <c>MessageStore = runtime.Storage</c> in its constructor, and <c>runtime.Storage</c> is the
/// placeholder <see cref="NullMessageStore"/> until <c>MessageStoreCollection.InitializeAsync()</c> runs
/// — which is deferred whenever <c>ResolveMainStoreOnConflict</c> (GH-3226) has to reconcile competing
/// Main claims, as it does for an event-store-integrated Main plus a database-backed queue transport.
/// </summary>
/// <remarks>
/// Both providers carry the identical capture and the identical fix; both need the test, because the
/// failure surfaces in provider-specific code (<c>resolveSqlServerMessageStore</c> here,
/// <c>MartenEnvelopeTransaction</c>'s constructor there) and a shared assertion would not have caught a
/// one-sided regression.
/// </remarks>
public class outboxed_session_factory_resolves_main_store_lazily
{
    [Fact]
    public async Task opens_a_session_when_main_was_settled_by_reconciliation()
    {
        using var host = await Host.CreateDefaultBuilder()
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

                // Claimant one: the SQL Server queue transport's own persistence.
                opts.UseSqlServerPersistenceAndTransport(
                        Servers.SqlServerConnectionString, "lazymain_q", "lazymain_q_queues")
                    .AutoProvision();

                // Claimant two: Polecat's integrated store.
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "lazymain";
                    })
                    .IntegrateWithWolverine(w => w.MessageStorageSchemaName = "lazymain_wolverine");

                opts.Durability.ResolveMainStoreOnConflict = mains =>
                    mains.FirstOrDefault(s => s.Uri.AbsolutePath.EndsWith("lazymain_q"));
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        runtime.Stores.Main.ShouldNotBeOfType<NullMessageStore>();

        var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();
        await using var session = factory.OpenSession(new MessageContext(runtime));

        session.ShouldNotBeNull();
    }

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
