using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Marten;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Xunit;

namespace PersistenceTests;

// GH-3359: in a mixed Marten + EF Core application, an entity mapped in a registered
// DbContext must resolve to the EF Core persistence provider no matter which integration
// the application registered first. MartenPersistenceFrameProvider.CanPersist claims every
// type (Marten genuinely can persist any document), so before the IsCatchAll ordering rule
// the winner was whichever integration happened to register last (InsertFirstPersistenceStrategy
// puts the last-applied extension at index 0). Both permutations below must behave identically.
public class persistence_provider_precedence_permutations
{
    // Deliberately NOT mapped in SampleDbContext, so only Marten can claim it
    public class MartenOnlyDocument
    {
        public Guid Id { get; set; }
    }

    private static async Task assertResolutionIsOrderIndependent(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var rules = runtime.Options.CodeGeneration;
        var container = host.Services.GetRequiredService<IServiceContainer>();

        // Item is mapped in SampleDbContext, so the selective EF Core provider owns it
        rules.TryFindPersistenceFrameProvider(container, typeof(Item), out var forItem)
            .ShouldBeTrue();
        forItem.ShouldBeOfType<EFCorePersistenceFrameProvider>();

        // Anything the DbContext model does not map still falls through to Marten
        rules.TryFindPersistenceFrameProvider(container, typeof(MartenOnlyDocument), out var forDocument)
            .ShouldBeTrue();
        forDocument.ShouldBeOfType<MartenPersistenceFrameProvider>();

        await host.StopAsync();
    }

    private static IHostBuilder builderWith(Action<WolverineOptions> registrations)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;
                opts.Discovery.DisableConventionalDiscovery();

                registrations(opts);

                opts.Services.AddResourceSetupOnStartup();
            });
    }

    [Fact]
    public async Task efcore_owns_its_mapped_entity_when_marten_registers_last()
    {
        // Marten last = Marten's extension applies last = MartenPersistenceFrameProvider at
        // index 0. This is the registration order that used to hand EF-mapped entities to Marten.
        using var host = await builderWith(opts =>
        {
            opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
                x.UseSqlServer(Servers.SqlServerConnectionString));

            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "provider_precedence";
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "provider_precedence");
        }).StartAsync();

        await assertResolutionIsOrderIndependent(host);
    }

    [Fact]
    public async Task efcore_owns_its_mapped_entity_when_marten_registers_first()
    {
        using var host = await builderWith(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "provider_precedence";
                })
                .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "provider_precedence");

            opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
                x.UseSqlServer(Servers.SqlServerConnectionString));
        }).StartAsync();

        await assertResolutionIsOrderIndependent(host);
    }
}
