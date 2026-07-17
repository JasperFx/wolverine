using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Marten.Events.Daemon.Coordination;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Distribution;
using Wolverine.Runtime.Agents;
using Xunit;

namespace MartenTests.Distribution;

// GH-3438: the ancillary Marten integration used to register only the concrete EventSubscriptionAgentFamily,
// never the IAgentFamily / IEventSubscriptionAgentFamily aliases. On a host whose only store wanting managed
// distribution is ancillary (here the main store deliberately does NOT opt in), nothing resolved the family
// through those interfaces — so Wolverine's managed distribution and external tooling (CritterWatch's
// projection admin) got an empty family. The ancillary path now registers the aliases when its own
// UseWolverineManagedEventSubscriptionDistribution flag is set.
public class ancillary_managed_distribution_family_registration : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("gh3438_main");
            await conn.DropSchemaAsync("gh3438_anc");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Main store provides node/envelope storage but deliberately does NOT opt into managed
                // distribution, so it registers no family aliases. Any that resolve must come from the
                // ancillary path.
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh3438_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IGh3438Store>(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "gh3438_anc";
                }).IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void the_event_subscription_agent_family_resolves_through_its_interface()
    {
        // The exact repro from the issue: empty before the fix.
        _host.Services.GetServices<IEventSubscriptionAgentFamily>().ShouldNotBeEmpty();
    }

    [Fact]
    public void the_family_is_registered_as_an_agent_family_for_distribution()
    {
        _host.Services.GetServices<IAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>()
            .ShouldNotBeEmpty();
    }

    [Fact]
    public void the_ancillary_store_gets_the_managed_projection_coordinator()
    {
        _host.Services.GetRequiredService<IProjectionCoordinator<IGh3438Store>>()
            .ShouldBeOfType<WolverineProjectionCoordinator<IGh3438Store>>();
    }
}

public interface IGh3438Store : IDocumentStore;
