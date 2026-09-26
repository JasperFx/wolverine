using Alba;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4516. A <b>missing</b> mandatory tenant id was already handled -- [RequiresTenant] stops the request
/// with a 400 ProblemDetails. An <b>unknown</b> one, present on the request but with no database or
/// registration behind it, threw UnknownTenantIdException, which Wolverine.Http did not catch, so the client
/// got a 500 for what is a client side error.
/// </summary>
public class unknown_tenant_problem_details_4516
{
    [Fact]
    public async Task an_unknown_tenant_id_is_a_404_problem_details()
    {
        await using var host = await startHostAsync(mapUnknownTenant: true);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/gh4516/tenanted?tenantId=ghost");
            x.StatusCodeShouldBe(404);
        });

        var problem = await result.ReadAsJsonAsync<ProblemDetails>();

        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(404);
        problem.Title.ShouldBe("Unknown tenant");
        problem.Detail.ShouldNotBeNull();
        problem.Detail.ShouldContain("ghost");
    }

    [Fact]
    public async Task a_known_tenant_is_untouched()
    {
        await using var host = await startHostAsync(mapUnknownTenant: true);

        var result = await host.Scenario(x =>
        {
            x.Get.Url("/gh4516/tenanted?tenantId=acme");
            x.StatusCodeShouldBeOk();
        });

        (await result.ReadAsTextAsync()).ShouldBe("acme");
    }

    [Fact]
    public async Task a_missing_tenant_id_still_answers_400_not_404()
    {
        // The two failures are genuinely different -- "you did not say which tenant" versus "the tenant you
        // named does not exist" -- and this mapping must not collapse them onto one status.
        await using var host = await startHostAsync(mapUnknownTenant: true);

        await host.Scenario(x =>
        {
            x.Get.Url("/gh4516/tenanted");
            x.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task without_the_opt_in_the_unknown_tenant_is_not_mapped_to_404()
    {
        // The point of this control is that MapUnknownTenantToNotFound() is what produces the 404, rather
        // than something else in the pipeline. It is NOT a claim about how the unmapped failure surfaces,
        // and the first version of this test accidentally made one: it asked Alba to assert a 404 inside a
        // scenario it expected to throw, which is self contradictory. Locally the UnknownTenantIdException
        // propagated before Alba evaluated assertions and the test passed; on CI the host turned it into a
        // 500 first, so Alba's own 404 assertion failed and raised ScenarioAssertionException instead --
        // red on every PR until this fix.
        //
        // Assert only what the opt in owns: without it, the response is not a mapped 404.
        await using var host = await startHostAsync(mapUnknownTenant: false);

        try
        {
            var response = await host.Scenario(x =>
            {
                x.Get.Url("/gh4516/tenanted?tenantId=ghost");
                x.IgnoreStatusCode();
            });

            response.Context.Response.StatusCode.ShouldNotBe(404);
        }
        catch (UnknownTenantIdException)
        {
            // The other way an unmapped failure surfaces: the exception escapes the scenario entirely.
            // Same conclusion, and deliberately the only exception type tolerated here -- anything else
            // still fails this test.
        }
    }

    [Fact]
    public async Task the_404_is_advertised_only_on_the_tenanted_chains()
    {
        await using var host = await startHostAsync(mapUnknownTenant: true);

        var chains = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains;

        var tenanted = chains.Single(x => x.Method.Method.Name == nameof(Gh4516Endpoint.Tenanted));
        tenanted.BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ShouldContain(404);

        // ...and a chain that resolves no tenant cannot fail to resolve one, so it must not advertise it
        var notTenanted = chains.Single(x => x.Method.Method.Name == nameof(Gh4516Endpoint.NotTenanted));
        notTenanted.BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>()
            .Select(x => x.StatusCode)
            .ShouldNotContain(404);
    }

    private static async Task<IAlbaHost> startHostAsync(bool mapUnknownTenant)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(unknown_tenant_problem_details_4516).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsQueryStringValue("tenantId");

            // Without AssertExists(), a missing tenant id binds *DEFAULT* and reaches the handler --
            // which is what the a_missing_tenant_id_still_answers_400_not_404 test is guarding.
            opts.TenantId.AssertExists();

            if (mapUnknownTenant)
            {
                opts.MapUnknownTenantToNotFound();
            }

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-4516 endpoint", type => type != typeof(Gh4516Endpoint)));
        }));
    }
}

public static class Gh4516Endpoint
{
    // Stands in for a store or tenant source that does not know the id. Anything that resolves a tenant
    // -- Marten's database source, Wolverine's own tenant sources -- throws this same exception.
    [RequiresTenant]
    [WolverineGet("/gh4516/tenanted")]
    public static string Tenanted(IMessageBus bus)
    {
        if (bus.TenantId != "acme")
        {
            throw new UnknownTenantIdException(bus.TenantId!);
        }

        return bus.TenantId!;
    }

    [NotTenanted]
    [WolverineGet("/gh4516/anonymous")]
    public static string NotTenanted() => "no tenant here";
}
