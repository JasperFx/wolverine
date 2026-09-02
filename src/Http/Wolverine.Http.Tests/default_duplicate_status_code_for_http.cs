using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// An application-wide default for the duplicate status code, so an app that wants every deduplicated
/// endpoint to answer something other than 409 says it once rather than on every attribute.
///
/// <para>
/// The interesting case is the one that is easy to get wrong: an endpoint asking explicitly for 409
/// must keep 409 even when the application default is something else. That is why the resolution is
/// keyed off whether a code was <i>stated</i> rather than off its value -- a sentinel comparison
/// against 409 would quietly override the endpoint that meant it.
/// </para>
/// </summary>
public class default_duplicate_status_code_for_http : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "http_dedup_default");

            opts.Durability.EnableMessageDeduplication = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(default_duplicate_status_code_for_http).Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            // The whole point of this fixture: one application-wide setting rather than an attribute
            // argument repeated on every endpoint.
            opts.DefaultDuplicateStatusCode = 422;

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a default-status test endpoint",
                    type => type != typeof(InheritsDefaultStatusEndpoint)
                            && type != typeof(InsistsOn409Endpoint)));
        }));

        await ((IHost)theHost).ResetResourceState();

        InheritsDefaultStatusEndpoint.Calls.Clear();
        InsistsOn409Endpoint.Calls.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task an_endpoint_that_states_nothing_inherits_the_application_default()
    {
        var key = Guid.NewGuid().ToString();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("first")).ToUrl("/dedup-default/inherits");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("replay")).ToUrl("/dedup-default/inherits");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(422);
        });

        InheritsDefaultStatusEndpoint.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task an_endpoint_that_explicitly_asks_for_409_keeps_409()
    {
        // The case a "is it still 409?" sentinel check would get wrong.
        var key = Guid.NewGuid().ToString();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("first")).ToUrl("/dedup-default/insists");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBeOk();
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new DedupRequest("replay")).ToUrl("/dedup-default/insists");
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(409);
        });

        InsistsOn409Endpoint.Calls.ShouldHaveSingleItem();
    }

    [Fact]
    public void the_application_default_is_advertised_in_the_endpoint_metadata()
    {
        // Metadata is registered inside the HttpChain constructor, before any IHttpPolicy runs, so the
        // default has to reach the chain earlier than a policy could deliver it. If this drifts, the
        // runtime answers 422 while the OpenAPI document still promises 409 -- a contract lie that no
        // request-level test would catch.
        var graph = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

        var inherits = graph.ChainFor("POST", "/dedup-default/inherits");
        inherits.ShouldNotBeNull();
        var inheritsMetadata = inherits.BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

        inheritsMetadata.Any(x => x.StatusCode == 422)
            .ShouldBeTrue("the application default must be discoverable from the OpenAPI document");
        inheritsMetadata.Any(x => x.StatusCode == 409)
            .ShouldBeFalse("409 was overridden by the application default and must not still be advertised");

        var insists = graph.ChainFor("POST", "/dedup-default/insists");
        insists.ShouldNotBeNull();
        var insistsMetadata = insists.BuildEndpoint(RouteWarmup.Lazy)
            .Metadata.OfType<IProducesResponseTypeMetadata>().ToArray();

        insistsMetadata.Any(x => x.StatusCode == 409)
            .ShouldBeTrue("an endpoint that stated 409 must still advertise 409");
    }
}

public static class InheritsDefaultStatusEndpoint
{
    public static readonly List<string> Calls = [];

    [Deduplicated]
    [WolverinePost("/dedup-default/inherits")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}

public static class InsistsOn409Endpoint
{
    public static readonly List<string> Calls = [];

    [Deduplicated(DuplicateStatusCode = 409)]
    [WolverinePost("/dedup-default/insists")]
    public static string Post(DedupRequest request)
    {
        Calls.Add(request.Name);
        return "ok";
    }
}
