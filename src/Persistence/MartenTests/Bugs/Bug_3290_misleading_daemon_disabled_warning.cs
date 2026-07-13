using IntegrationTests;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;

namespace MartenTests.Bugs;

// GH-3290: with UseWolverineManagedEventSubscriptionDistribution = true, Marten's
// DocumentStore wrote a misleading "The async daemon is disabled ... will not be
// executed" console warning at startup even though Wolverine's managed distribution
// does run the async projections. The Marten integration now records the real daemon
// state as DaemonMode.ExternallyManaged (jasperfx#490) — same runtime posture as
// Disabled (no Marten-hosted coordinator, no Marten-side agents), but the warning
// gate stays quiet because the external host runs the projections.
public class Bug_3290_misleading_daemon_disabled_warning : PostgresqlContext
{
    private static IHostBuilder configureHost(bool useWolverineManagedDistribution,
        DaemonMode? explicitDaemonMode = null,
        bool explicitDaemonBeforeIntegration = false)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var marten = opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "bug3290";

                    m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                });

                if (explicitDaemonMode.HasValue && explicitDaemonBeforeIntegration)
                {
                    marten.AddAsyncDaemon(explicitDaemonMode.Value);
                }

                marten.IntegrateWithWolverine(m =>
                {
                    m.UseWolverineManagedEventSubscriptionDistribution = useWolverineManagedDistribution;
                });

                if (explicitDaemonMode.HasValue && !explicitDaemonBeforeIntegration)
                {
                    marten.AddAsyncDaemon(explicitDaemonMode.Value);
                }
            });
    }

    // Marten writes the async-daemon warning with Console.WriteLine inside the
    // DocumentStore constructor, so capture stdout around the first resolution of
    // the store. The MartenTests assembly runs a single collection (no parallel
    // test classes), so swapping Console.Out here is safe.
    private static (DocumentStore Store, string ConsoleOutput) buildStoreCapturingConsole(IHost host)
    {
        var original = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            var store = host.Services.GetRequiredService<IDocumentStore>().As<DocumentStore>();
            return (store, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    [Fact]
    public void managed_distribution_records_externally_managed_and_does_not_warn()
    {
        using var host = configureHost(useWolverineManagedDistribution: true).Build();

        var (store, console) = buildStoreCapturingConsole(host);

        // Wolverine's managed distribution replaces Marten's own daemon coordination
        // outright, so Marten should see the daemon as externally managed...
        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.ExternallyManaged);

        // ...and must not claim the async projections will not be executed
        console.ShouldNotContain("The async daemon is disabled");
    }

    [Fact]
    public void managed_distribution_starts_no_marten_hosted_coordinator()
    {
        using var host = configureHost(useWolverineManagedDistribution: true).Build();

        buildStoreCapturingConsole(host);

        // The whole point of ExternallyManaged over #3329's HotCold: nothing
        // Marten-hosted starts. Marten's own ProjectionCoordinator only ever enters
        // the container as an IHostedService via AddAsyncDaemon(), so no registered
        // hosted service may be a projection coordinator of any flavor.
        host.Services.GetServices<IHostedService>()
            .OfType<JasperFx.Events.Daemon.IProjectionCoordinator>()
            .ShouldBeEmpty();

        // Marten's coordinator interface resolves to Wolverine's distribution-backed
        // coordinator (a plain singleton, never hosted), not Marten's ProjectionCoordinator
        host.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>()
            .ShouldBeOfType<WolverineProjectionCoordinator>();
    }

    [Fact]
    public void without_managed_distribution_the_warning_still_fires()
    {
        using var host = configureHost(useWolverineManagedDistribution: false).Build();

        var (store, console) = buildStoreCapturingConsole(host);

        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.Disabled);
        console.ShouldContain("The async daemon is disabled");
    }

    // GH-3388 — these two used to assert that an explicit Solo/HotCold daemon choice SURVIVES
    // alongside Wolverine-managed distribution ("an explicit user choice is never overwritten").
    // That contract turned out to be the footgun: the combination is never valid. Marten hosts its
    // own ProjectionCoordinator (an IHostedService) which starts the shards, while Wolverine's
    // distribution drives the same daemon through its own coordinator. The two compete, and the
    // symptom is not an error but a HANG — a tracked catch-up that never completes. A user lost a
    // day to it, and nothing in the stalling code points back at the registration that caused it.
    // Preserving the choice silently is strictly worse than refusing it, so it is now refused.
    // Rejected at host START, not at store construction: Marten applies AddAsyncDaemon's mode
    // through ConfigureMarten, which runs in registration order, so a daemon choice made AFTER
    // IntegrateWithWolverine is not yet visible while the integration configures. By start-up the
    // options are final — which is why BOTH call orders below are caught.
    [Fact]
    public async Task explicit_solo_daemon_after_integration_is_rejected()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.Solo).Build();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());

        ex.Message.ShouldContain("UseWolverineManagedEventSubscriptionDistribution");
        ex.Message.ShouldContain("MartenDaemonModeIsSolo");
    }

    [Fact]
    public async Task explicit_solo_daemon_before_integration_is_rejected()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.Solo,
            explicitDaemonBeforeIntegration: true).Build();

        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public async Task explicit_hotcold_daemon_is_rejected_too()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.HotCold).Build();

        await Should.ThrowAsync<InvalidOperationException>(() => host.StartAsync());
    }

    [Fact]
    public void an_explicit_daemon_choice_is_fine_without_managed_distribution()
    {
        // Nothing to compete with, so the user's choice stands exactly as before.
        using var host = configureHost(useWolverineManagedDistribution: false, DaemonMode.Solo).Build();

        var (store, _) = buildStoreCapturingConsole(host);

        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.Solo);
    }
}
