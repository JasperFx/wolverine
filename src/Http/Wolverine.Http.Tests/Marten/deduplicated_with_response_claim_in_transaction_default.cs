using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5160. <see cref="WolverineHttpOptions.DefaultClaimInTransaction" /> turns <c>ClaimInTransaction</c> on for every
/// <c>[DeduplicatedWithResponse]</c> endpoint that does not set it, and an endpoint that sets it, either way, keeps
/// its own choice.
/// </summary>
public class deduplicated_with_response_claim_in_transaction_default : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.EnableDeduplicatedResponses = true;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();

            // The application assembly is cached process-wide; include this one explicitly.
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_claim_in_transaction_default).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "http_dedup_response_default";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            #region sample_default_claim_in_transaction

            // Every [DeduplicatedWithResponse] endpoint that doesn't say otherwise
            // writes its claim inside its own transaction where it can
            opts.DefaultClaimInTransaction = true;

            #endregion

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a ClaimInTransaction default test endpoint",
                    type => type != typeof(DefaultClaimInTransactionEndpoints)));
        }));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task an_endpoint_that_says_nothing_takes_the_application_default()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("unset-" + key);

        var first = await postAsync("/claim-default/unset", request, key);
        var repeat = await postAsync("/claim-default/unset", request, key);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        var source = sourceFor("/claim-default/unset");
        source.ShouldContain(".QueueClaim(");
        source.ShouldNotContain(".TryClaimAsync(");
    }

    [Fact]
    public async Task an_endpoint_that_says_false_keeps_claim_and_release()
    {
        await postAsync("/claim-default/off", new DeduplicatedOrder("off"), Guid.NewGuid().ToString());

        var source = sourceFor("/claim-default/off");
        source.ShouldContain(".TryClaimAsync(");
        source.ShouldNotContain(".QueueClaim(");
    }

    [Fact]
    public async Task an_endpoint_that_says_true_claims_in_the_transaction()
    {
        await postAsync("/claim-default/on", new DeduplicatedOrder("on"), Guid.NewGuid().ToString());

        var source = sourceFor("/claim-default/on");
        source.ShouldContain(".QueueClaim(");
        source.ShouldNotContain(".TryClaimAsync(");
    }

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key) where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.StatusCodeShouldBe(201);
        });

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();
}

public class claim_in_transaction_on_the_attribute
{
    [Fact]
    public void unset_is_not_an_explicit_choice()
    {
        var attribute = new DeduplicatedWithResponseAttribute(DeduplicationScope.User);
        attribute.ClaimInTransaction.ShouldBeFalse();

        var chain = HttpChain.ChainFor(typeof(DefaultClaimInTransactionEndpoints),
            nameof(DefaultClaimInTransactionEndpoints.PostUnset));
        chain.DeduplicatedWithResponse.ShouldNotBeNull().ExplicitClaimInTransaction.ShouldBeNull();
    }

    [Fact]
    public void false_is_an_explicit_choice()
    {
        var chain = HttpChain.ChainFor(typeof(DefaultClaimInTransactionEndpoints),
            nameof(DefaultClaimInTransactionEndpoints.PostOff));
        chain.DeduplicatedWithResponse.ShouldNotBeNull().ExplicitClaimInTransaction.ShouldBe(false);
    }
}

public static class DefaultClaimInTransactionEndpoints
{
    #region sample_override_default_claim_in_transaction

    // Takes WolverineHttpOptions.DefaultClaimInTransaction
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/claim-default/unset")]
    public static DeduplicatedOrderCreated PostUnset(DeduplicatedOrder request, IDocumentSession session)
        => store(request, session);

    // Keeps claim-and-release whatever the application default is
    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = false)]
    [WolverinePost("/claim-default/off")]
    public static DeduplicatedOrderCreated PostOff(DeduplicatedOrder request, IDocumentSession session)
        => store(request, session);

    #endregion

    [DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
    [WolverinePost("/claim-default/on")]
    public static DeduplicatedOrderCreated PostOn(DeduplicatedOrder request, IDocumentSession session)
        => store(request, session);

    private static DeduplicatedOrderCreated store(DeduplicatedOrder request, IDocumentSession session)
    {
        var document = new DeduplicatedOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new DeduplicatedOrderCreated(document.Id);
    }
}
