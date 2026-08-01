using Alba;
using IntegrationTests;
using Marten;
using Marten.Exceptions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Http.Marten;
using Wolverine.Marten;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class concurrency_exception_problem_details(AppFixture fixture) : IntegrationContext(fixture)
{
    private async Task<Guid> startOrder()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status = await result.ReadAsJsonAsync<OrderStatus>();
        status.ShouldNotBeNull();
        return status.OrderId;
    }

    [Fact]
    public async Task stale_expected_version_responds_with_409_problem_details()
    {
        var id = await startOrder();

        // First time is fine, and advances the stream past version 1
        await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Socks", 1)).ToUrl("/orders/itemready");
        });

        // Replaying the same expected version trips Marten's optimistic concurrency
        // check when the session is committed
        var result = await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Shoes", 1)).ToUrl("/orders/itemready");
            x.StatusCodeShouldBe(409);
            x.ContentTypeShouldBe("application/problem+json");
        });

        // And let's verify that we got what we expected for the ProblemDetails
        // in the HTTP response body of the 2nd request
        var details = await result.ReadAsJsonAsync<ProblemDetails>();
        details.ShouldNotBeNull();
        details.Status.ShouldBe(409);
        details.Title.ShouldBe("Concurrency conflict");
    }

    [Fact]
    public async Task user_defined_on_exception_for_the_same_exception_type_wins()
    {
        var id = await startOrder();

        await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Socks", 1)).ToUrl("/orders/itemready/custom-handled");
        });

        // The endpoint's own OnException(ConcurrencyException) is already in the catch
        // block, so the policy must leave it alone rather than doubling the catch
        var result = await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Shoes", 1)).ToUrl("/orders/itemready/custom-handled");
            x.StatusCodeShouldBe(400);
        });

        var details = await result.ReadAsJsonAsync<ProblemDetails>();
        details.ShouldNotBeNull();
        details.Title.ShouldBe("Somebody else got there first");
    }

    [Fact]
    public void concurrency_conflict_is_registered_in_openapi_metadata()
    {
        var endpoint = endpointFor("/orders/itemready");

        var produces = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(x => x.StatusCode == 409);
        produces.Type.ShouldBe(typeof(ProblemDetails));
        produces.ContentTypes.Single().ShouldBe("application/problem+json");
    }

    [Fact]
    public void no_catch_or_conflict_metadata_added_when_the_endpoint_handles_the_exception_itself()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/itemready/custom-handled");
        chain.ShouldNotBeNull();

        // The single ConcurrencyException catch belongs to the endpoint's own OnException handler,
        // and this optimistic chain gets no StreamLockedException catch either
        var catchBlocks = chain.GetOrCreateTryCatchFinallyFrame().CatchBlocks;
        catchBlocks.Single().ExceptionType.ShouldBe(typeof(JasperFx.ConcurrencyException));

        // Since the policy added nothing here, it must not advertise the conflict status either
        endpointFor("/orders/itemready/custom-handled").Metadata.OfType<IProducesResponseTypeMetadata>()
            .Any(x => x.StatusCode == 409).ShouldBeFalse();
    }

    [Fact]
    public void stream_locked_catch_is_not_added_to_optimistic_chains()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/itemready");
        chain.ShouldNotBeNull();

        chain.GetOrCreateTryCatchFinallyFrame().CatchBlocks
            .Any(x => x.ExceptionType == typeof(StreamLockedException)).ShouldBeFalse();
    }

    private RouteEndpoint endpointFor(string route)
    {
        return Host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.RoutePattern.RawText == route && x.Metadata.OfType<HttpMethodMetadata>()
                .Any(m => m.HttpMethods.Contains("POST")));
    }
}

// The opt-in behavior itself needs hosts with different policy registrations than the shared
// WolverineWebApi application, so these tests bootstrap their own little applications against
// the endpoints at the bottom of this file
public class concurrency_exception_policy_opt_in
{
    private static async Task<IAlbaHost> buildHostAsync(Action<WolverineHttpOptions>? configure,
        string? connectionString = null)
    {
        // WolverineWebApi runs in Development under Alba too; without the developer exception
        // page an escaping exception surfaces to Alba as a thrown exception instead of a 500
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(concurrency_exception_policy_opt_in).Assembly);

            opts.Services.AddMarten(m =>
            {
                m.Connection(connectionString ?? Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "concurrency_policy_http";
                m.DisableNpgsqlLogging = true;
            }).IntegrateWithWolverine();

            opts.Policies.AutoApplyTransactions();
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts => configure?.Invoke(opts)));
    }

    private static async Task<Guid> startOrder(IAlbaHost host)
    {
        var result = await host.Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/local/orders/create");
        });

        var status = await result.ReadAsJsonAsync<OrderStatus>();
        status.ShouldNotBeNull();
        return status.OrderId;
    }

    [Fact]
    public async Task concurrency_exception_still_escapes_as_500_without_the_opt_in()
    {
        await using var host = await buildHostAsync(null);

        var id = await startOrder(host);

        await host.Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Socks", 1)).ToUrl("/local/orders/itemready");
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Shoes", 1)).ToUrl("/local/orders/itemready");
            x.StatusCodeShouldBe(500);
        });
    }

    [Fact]
    public async Task uses_a_non_default_status_code_in_both_response_and_metadata()
    {
        await using var host = await buildHostAsync(opts => opts.UseProblemDetailsForConcurrencyExceptions(412));

        var id = await startOrder(host);

        await host.Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Socks", 1)).ToUrl("/local/orders/itemready");
        });

        var result = await host.Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Shoes", 1)).ToUrl("/local/orders/itemready");
            x.StatusCodeShouldBe(412);
            x.ContentTypeShouldBe("application/problem+json");
        });

        var details = await result.ReadAsJsonAsync<ProblemDetails>();
        details.ShouldNotBeNull();
        details.Status.ShouldBe(412);

        var endpoint = host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>().Single(x => x.RoutePattern.RawText == "/local/orders/itemready");
        endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(x => x.StatusCode == 412)
            .ContentTypes.Single().ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task stream_locked_by_a_competing_session_responds_with_409()
    {
        // Marten surfaces a contended FetchForExclusiveWriting as a StreamLockedException only
        // after the Npgsql command timeout, so keep that short to make this test quick -- but not
        // so short that first-touch schema migration under parallel suite load can trip it
        await using var host = await buildHostAsync(opts => opts.UseProblemDetailsForConcurrencyExceptions(),
            Servers.PostgresConnectionString + ";Command Timeout=5");

        var id = await startOrder(host);

        // Chain-level pin: the StreamLockedException catch is only added to exclusive chains
        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        graph.ChainFor("POST", "/local/orders/ship-exclusive")!.GetOrCreateTryCatchFinallyFrame().CatchBlocks
            .Any(x => x.ExceptionType == typeof(StreamLockedException)).ShouldBeTrue();
        graph.ChainFor("POST", "/local/orders/itemready")!.GetOrCreateTryCatchFinallyFrame().CatchBlocks
            .Any(x => x.ExceptionType == typeof(StreamLockedException)).ShouldBeFalse();

        // Hold the stream's exclusive lock from a competing session for the duration of the request
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using (var holder = store.LightweightSession())
        {
            await holder.Events.FetchForExclusiveWriting<Order>(id, TestContext.Current.CancellationToken);

            var result = await host.Scenario(x =>
            {
                x.Post.Json(new ShipOrderExclusively(id)).ToUrl("/local/orders/ship-exclusive");
                x.StatusCodeShouldBe(409);
                x.ContentTypeShouldBe("application/problem+json");
            });

            var details = await result.ReadAsJsonAsync<ProblemDetails>();
            details.ShouldNotBeNull();
            details.Status.ShouldBe(409);
        }
    }
}

public static class LocalConcurrencyOrderEndpoints
{
    [Transactional]
    [WolverinePost("/local/orders/create")]
    public static OrderStatus Start(StartOrder command, IDocumentSession session)
    {
        var items = command.Items.Select(x => new Item { Name = x }).ToArray();
        var orderId = session.Events.StartStream<Order>(new OrderCreated(items)).Id;

        return new OrderStatus(orderId, false);
    }

    [AggregateHandler]
    [WolverinePost("/local/orders/itemready")]
    public static (OrderStatus, Events) Post(MarkItemReady command, Order order)
    {
        return (new OrderStatus(order.Id, order.IsReadyToShip()), [new ItemReady(command.ItemName)]);
    }

    [AggregateHandler(ConcurrencyStyle.Exclusive)]
    [WolverinePost("/local/orders/ship-exclusive"), EmptyResponse]
    public static OrderShipped Ship(ShipOrderExclusively command, Order order)
    {
        return new OrderShipped();
    }
}

public record ShipOrderExclusively(Guid OrderId);
