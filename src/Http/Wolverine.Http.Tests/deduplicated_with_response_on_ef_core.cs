using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests;

// GH-4742. [DeduplicatedWithResponse] on an EF Core endpoint: the response must be recorded after the DbContext
// commits.
public class deduplicated_with_response_on_ef_core : IAsyncLifetime
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
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_deduplicated_response_ef");
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_on_ef_core).Assembly);
        });

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the EF Core replay test endpoint",
                    type => type != typeof(EfCoreDeduplicatedResponseEndpoint)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_repeat_is_answered_with_the_first_response_and_the_item_is_written_once()
    {
        var key = Guid.NewGuid().ToString();
        var name = "ef-" + key;

        var first = await postAsync(name, key, 201);
        var repeat = await postAsync(name, key, 201);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        using var scope = theHost.Services.CreateScope();
        var items = scope.ServiceProvider.GetRequiredService<ItemsDbContext>().Items;
        (await items.CountAsync(x => x.Name == name, TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task the_response_is_recorded_after_the_commit()
    {
        // Warm the route with a real request, then read the source it was built from.
        await postAsync("ef-warm-" + Guid.NewGuid(), Guid.NewGuid().ToString(), 201);

        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var source = graph.Chains.Single(x => x.RoutePattern!.RawText == "/ef-deduplicated-response/items").SourceCode.ShouldNotBeNull();

        var claim = source.IndexOf(".TryClaimAsync(", StringComparison.Ordinal);
        var commit = source.IndexOf(".SaveChangesAsync(", StringComparison.Ordinal);
        var record = source.IndexOf(".RecordResponseAsync(", StringComparison.Ordinal);

        claim.ShouldBeGreaterThan(-1, source);
        claim.ShouldBeLessThan(commit, source);
        commit.ShouldBeLessThan(record, source);
    }

    private Task<IScenarioResult> postAsync(string name, string key, int status)
        => theHost.Scenario(x =>
        {
            x.Post.Json(new CreateItemCommand { Name = name }).ToUrl("/ef-deduplicated-response/items");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(status);
        });
}

public record EfCoreDeduplicatedItemCreated(Guid Id) : CreationResponse($"/ef-deduplicated-response/items/{Id}");

public static class EfCoreDeduplicatedResponseEndpoint
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/ef-deduplicated-response/items")]
    public static EfCoreDeduplicatedItemCreated Post(CreateItemCommand command, ItemsDbContext db)
    {
        var item = new Item { Id = Guid.NewGuid(), Name = command.Name };
        db.Items.Add(item);
        return new EfCoreDeduplicatedItemCreated(item.Id);
    }
}
