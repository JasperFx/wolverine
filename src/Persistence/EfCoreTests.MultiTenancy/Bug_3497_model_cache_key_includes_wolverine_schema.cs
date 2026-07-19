using IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SharedPersistenceModels.Items;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Xunit;

namespace EfCoreTests.MultiTenancy;

// GH-3497: EF caches the built model per context type by default, so two hosts in one
// process sharing a DbContext type but using different Wolverine durability schemas
// silently shared whichever envelope-table mapping was built first -- the second host
// wrote envelopes into the first host's schema (surfaced as ordering-dependent
// failures in this very test assembly, where Bug_2739's unprovisioned bug2739_master
// schema leaked into the SQL Server multi-tenancy suites)
[Collection("multi-tenancy")]
public class Bug_3497_model_cache_key_includes_wolverine_schema
{
    [Fact]
    public async Task two_hosts_with_different_wolverine_schemas_get_distinct_envelope_mappings()
    {
        using var hostA = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3497_a");
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString));
            }).StartAsync();

        using var hostB = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "bug3497_b");
                opts.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(
                    x => x.UseNpgsql(Servers.PostgresConnectionString));
            }).StartAsync();

        using var scopeA = hostA.Services.CreateScope();
        using var scopeB = hostB.Services.CreateScope();

        var schemaA = envelopeSchemaOf(scopeA.ServiceProvider.GetRequiredService<ItemsDbContext>());
        var schemaB = envelopeSchemaOf(scopeB.ServiceProvider.GetRequiredService<ItemsDbContext>());

        schemaA.ShouldBe("bug3497_a");
        schemaB.ShouldBe("bug3497_b");
    }

    private static string? envelopeSchemaOf(DbContext context)
    {
        var incoming = context.Model.GetEntityTypes()
            .Single(x => x.ClrType.Name == "IncomingMessage");
        return incoming.GetSchema();
    }
}
