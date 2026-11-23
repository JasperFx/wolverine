using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests;

public class deduplication_on_an_ef_core_transaction : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddDbContextWithWolverineIntegration<ItemsDbContext>(x =>
            x.UseNpgsql(Servers.PostgresConnectionString));

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_dedup_ef");
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplication_on_an_ef_core_transaction).Assembly);
        });

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the EF Core deduplication test endpoint",
                    type => type != typeof(EfCoreDeduplicatedEndpoint)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_committed_claim_counts_as_claimed_and_its_repeat_as_a_duplicate()
    {
        using var outcomes = new DeduplicationOutcomeListener(theHost);
        var key = Guid.NewGuid().ToString();

        await postAsync("ef-dedup-" + key, key, 200);
        await postAsync("ef-dedup-" + key, key, 409);

        outcomes.On("/ef-dedup/items").ShouldBe(["claimed", "duplicate"]);
    }

    private Task<IScenarioResult> postAsync(string name, string key, int status)
        => theHost.Scenario(x =>
        {
            x.Post.Json(new CreateItemCommand { Name = name }).ToUrl("/ef-dedup/items");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(status);
        });
}

public static class EfCoreDeduplicatedEndpoint
{
    [Deduplicated]
    [WolverinePost("/ef-dedup/items")]
    public static string Post(CreateItemCommand command, ItemsDbContext db)
    {
        db.Items.Add(new Item { Id = Guid.NewGuid(), Name = command.Name });
        return "ok";
    }
}
