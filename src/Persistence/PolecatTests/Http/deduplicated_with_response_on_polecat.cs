using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Polecat;

namespace PolecatTests.Http;

/// <summary>
/// GH-5835 / GH-5165 on Polecat. <c>[DeduplicatedWithResponse]</c> claims and releases on Polecat, and Polecat now
/// tells the chain when its session committed. A commit call that throws after the database committed keeps the
/// claim over the committed work, so the client's retry doesn't run the endpoint again.
/// </summary>
public class deduplicated_with_response_on_polecat : IAsyncLifetime
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
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_on_polecat).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "http_dedup_response_polecat";
            m.Listeners.Add(new FailAfterCommitPolecatListener());
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a Polecat [DeduplicatedWithResponse] test endpoint",
                    type => type != typeof(PolecatDeduplicatedResponseEndpoints)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_repeat_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();
        var name = "replayed-" + key;

        var first = await postAsync("/polecat-dedup-response/create", name, key, 200);
        var repeat = await postAsync("/polecat-dedup-response/create", name, key, 200);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        PolecatDeduplicatedResponseEndpoints.Runs(name).ShouldBe(1);
    }

    [Fact]
    public async Task a_failure_inside_the_commit_after_the_database_commit_keeps_the_claim()
    {
        var key = Guid.NewGuid().ToString();
        var name = "after-commit-" + key;

        // A 500 under the developer exception page, else the listener's exception straight through the test server
        try
        {
            var failed = await postAsync("/polecat-dedup-response/after-commit-failure", name, key, null);
            failed.Context.Response.StatusCode.ShouldBeGreaterThanOrEqualTo(500);
        }
        catch (DivideByZeroException)
        {
        }

        // Kept, unanswered: the work is done, but there is no response to replay
        await postAsync("/polecat-dedup-response/after-commit-failure", name, key, 409);
        PolecatDeduplicatedResponseEndpoints.Runs(name).ShouldBe(1);

        sourceFor("/polecat-dedup-response/after-commit-failure")
            .ShouldContain(nameof(HttpHandler.KeepDeduplicatedClaimIfCommitted));
    }

    private Task<IScenarioResult> postAsync(string url, string name, string key, int? status)
        => theHost.Scenario(x =>
        {
            x.Post.Json(new PolecatDedupRequest(name)).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            if (status.HasValue)
            {
                x.StatusCodeShouldBe(status.Value);
            }
            else
            {
                x.IgnoreStatusCode();
            }
        });

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();
}

public record PolecatDedupResponse(Guid Id, string Name);

public static class PolecatDeduplicatedResponseEndpoints
{
    private static readonly ConcurrentDictionary<string, int> _runs = new();

    public static int Runs(string name) => _runs.TryGetValue(name, out var count) ? count : 0;

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/polecat-dedup-response/create")]
    public static PolecatDedupResponse Create(PolecatDedupRequest request, IDocumentSession session)
    {
        _runs.AddOrUpdate(request.Name, 1, (_, count) => count + 1);
        var document = new PolecatDedupDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new PolecatDedupResponse(document.Id, request.Name);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/polecat-dedup-response/after-commit-failure")]
    public static PolecatDedupResponse AfterCommitFailure(PolecatDedupRequest request, IDocumentSession session)
    {
        _runs.AddOrUpdate(request.Name, 1, (_, count) => count + 1);
        session.Insert(new FailAfterCommitPolecatMarker { Id = Guid.NewGuid() });
        return new PolecatDedupResponse(Guid.NewGuid(), request.Name);
    }
}
