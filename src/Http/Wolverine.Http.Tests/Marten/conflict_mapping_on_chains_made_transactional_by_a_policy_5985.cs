using System.Text.Json;
using Alba;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5985. The conflict mapping's default filter is <c>chain.IsTransactional</c>, and it used to be applied through
/// <c>AddMiddleware</c>, whose filters run before the chain policies. <c>AutoApplyTransactions</c> is a chain policy,
/// so a chain it made transactional (one that takes an <see cref="IDocumentSession" />) missed the middleware, and a
/// concurrency failure answered 500 while the OpenAPI document advertised a 409.
/// </summary>
public class conflict_mapping_on_chains_made_transactional_by_a_policy_5985 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "conflict_after_policies";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);

            // What makes the endpoint below transactional, and only once the chain policies run
            opts.Policies.AutoApplyTransactions();
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                // The default filter, ConflictMapping.IsTransactional. Explicit so this holds on 6.x as well, where
                // the mapping is not on by default
                opts.MapConcurrencyFailuresToConflict();

                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the GH-5985 endpoints",
                        type => type != typeof(RenameOrder5985Endpoints)));
            });
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    private async Task<Guid> createOrderAsync()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/orders-5985/create");
            x.StatusCodeShouldBeOk();
        });

        return JsonSerializer.Deserialize<Guid>(await result.ReadAsTextAsync());
    }

    [Fact]
    public async Task a_stale_version_on_a_chain_auto_apply_transactions_made_transactional_is_a_409()
    {
        var id = await createOrderAsync();

        var result = await theHost.Scenario(x =>
        {
            // The stream is at version 1, so expecting 5 loses the race
            x.Post.Json(new RenameOrder5985(id, 5, "renamed")).ToUrl("/orders-5985/rename");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var problem = await result.ReadAsJsonAsync<ProblemDetails>();
        problem.Title.ShouldBe("Conflict");
    }

    [Fact]
    public async Task the_current_version_is_written()
    {
        var id = await createOrderAsync();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new RenameOrder5985(id, 1, "renamed")).ToUrl("/orders-5985/rename");
            x.StatusCodeShouldBe(204);
        });
    }

    [Fact]
    public void the_middleware_and_the_openapi_metadata_agree()
    {
        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ChainFor("POST", "/orders-5985/rename")!;

        chain.IsTransactional.ShouldBeTrue();
        chain.BuildEndpoint(RouteWarmup.Lazy).Metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(x => x.StatusCode == 409);
    }
}

public record Order5985Created(string Name);

public record Order5985Renamed(string Name);

public class Order5985
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    public void Apply(Order5985Created e) => Name = e.Name;
    public void Apply(Order5985Renamed e) => Name = e.Name;
}

public record RenameOrder5985(Guid OrderId, long Version, string Name);

public static class RenameOrder5985Endpoints
{
    [WolverinePost("/orders-5985/create")]
    public static Guid Create(IDocumentSession session)
    {
        return session.Events.StartStream<Order5985>(new Order5985Created("first")).Id;
    }

    // Transactional only because it takes an IDocumentSession and AutoApplyTransactions is on
    [WolverinePost("/orders-5985/rename")]
    [EmptyResponse]
    public static async Task Rename(RenameOrder5985 command, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<Order5985>(command.OrderId, command.Version);
        stream.AppendOne(new Order5985Renamed(command.Name));
    }
}
