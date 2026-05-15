using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Events.Aggregation;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Xunit;

namespace PolecatTests.Publishing;

/// <summary>
///     Registration smoke test for wolverine#2774: verify that
///     <see cref="WolverineOptionsPolecatExtensions.IntegrateWithWolverine"/>
///     replaces Polecat's default <see cref="NulloMessageOutbox"/> on
///     <see cref="StoreOptions.Events"/>.<c>MessageOutbox</c> with the Wolverine
///     bridge (<see cref="PolecatToWolverineOutbox"/>).
///
///     Doesn't drive the daemon end-to-end — that's the broader test-parity
///     work tracked alongside the issue's other acceptance criteria. This test
///     fails closed if the registration line in <c>PolecatOverrides.Configure</c>
///     ever regresses (silent drop back to the no-op outbox would surface here
///     as the assertion failing, not as messages silently disappearing in
///     production).
/// </summary>
public class polecat_to_wolverine_outbox_registration
{
    [Fact]
    public async Task integrate_with_wolverine_replaces_the_default_nullo_outbox()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "wolverine_polecat_outbox_smoke";
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();

        // Before #2774, Polecat shipped NulloMessageOutbox as the default; the bridge
        // flips it to PolecatToWolverineOutbox during PolecatOverrides.Configure.
        store.Options.Events.MessageOutbox.ShouldBeOfType<PolecatToWolverineOutbox>();
    }

    [Fact]
    public async Task polecat_without_integrate_with_wolverine_keeps_polecats_default_outbox()
    {
        // Standalone Polecat host (no Wolverine integration) should keep Polecat's
        // default outbox — confirms PolecatOverrides only flips the outbox when
        // IntegrateWithWolverine actually runs. NulloMessageOutbox is internal to
        // Polecat so we assert the negative (NOT the Wolverine bridge) instead of
        // type-checking against the internal default.
        using var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "polecat_no_wolverine_outbox_smoke";
                });

                services.AddResourceSetupOnStartup();
            })
            .Build();

        await host.StartAsync();

        try
        {
            var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
            store.Options.Events.MessageOutbox.ShouldNotBeOfType<PolecatToWolverineOutbox>();
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
