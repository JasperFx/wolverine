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

namespace MartenTests.Bugs;

// GH-3290: with UseWolverineManagedEventSubscriptionDistribution = true, Marten's
// DocumentStore wrote a misleading "The async daemon is disabled ... will not be
// executed" console warning at startup even though Wolverine's managed distribution
// does run the async projections. The Marten integration now reflects the real daemon
// state on StoreOptions.Projections.AsyncMode so the warning is not raised.
public class Bug_3290_misleading_daemon_disabled_warning : PostgresqlContext
{
    private static IHostBuilder configureHost(bool useWolverineManagedDistribution,
        DaemonMode? explicitDaemonMode = null)
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
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        m.UseWolverineManagedEventSubscriptionDistribution = useWolverineManagedDistribution;
                    });

                if (explicitDaemonMode.HasValue)
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
    public void managed_distribution_reflects_daemon_state_and_does_not_warn()
    {
        using var host = configureHost(useWolverineManagedDistribution: true).Build();

        var (store, console) = buildStoreCapturingConsole(host);

        // Wolverine's managed distribution replaces AddAsyncDaemon(DaemonMode.HotCold),
        // so Marten should see the equivalent daemon mode...
        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.HotCold);

        // ...and must not claim the async projections will not be executed
        console.ShouldNotContain("The async daemon is disabled");
    }

    [Fact]
    public void without_managed_distribution_the_warning_still_fires()
    {
        using var host = configureHost(useWolverineManagedDistribution: false).Build();

        var (store, console) = buildStoreCapturingConsole(host);

        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.Disabled);
        console.ShouldContain("The async daemon is disabled");
    }

    [Fact]
    public void explicit_add_async_daemon_choice_is_not_overwritten()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.Solo).Build();

        var (store, _) = buildStoreCapturingConsole(host);

        store.Options.Projections.AsyncMode.ShouldBe(DaemonMode.Solo);
    }
}
