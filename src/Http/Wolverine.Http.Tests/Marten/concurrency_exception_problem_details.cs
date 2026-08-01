using Alba;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
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
        var endpoints = Host.Services.GetServices<EndpointDataSource>().SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>().ToList();

        var endpoint = endpoints.Single(x =>
            x.RoutePattern.RawText == "/orders/itemready" && x.Metadata.OfType<HttpMethodMetadata>()
                .Any(m => m.HttpMethods.Contains("POST")));

        var produces = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>().Single(x => x.StatusCode == 409);
        produces.Type.ShouldBe(typeof(ProblemDetails));
        produces.ContentTypes.Single().ShouldBe("application/problem+json");
    }

    [Fact]
    public void does_not_register_the_conflict_status_when_the_user_catch_took_the_exception_type()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/itemready/custom-handled");
        chain.ShouldNotBeNull();

        // The StreamLockedException catch is still added, but ConcurrencyException
        // belongs to the endpoint's own OnException handler
        var catchTypes = chain.GetOrCreateTryCatchFinallyFrame().CatchBlocks
            .Count(x => x.ExceptionType == typeof(JasperFx.ConcurrencyException));
        catchTypes.ShouldBe(1);
    }
}
