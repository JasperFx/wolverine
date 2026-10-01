using Alba;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4742. [DeduplicatedWithResponse] on endpoints that commit through Marten: the response is recorded after
/// the commit, and the fingerprint still reads a body that the audit frame or aggregate id inference read first.
/// </summary>
public class deduplicated_with_response_on_marten : IAsyncLifetime
{
    private const string SchemaName = "http_deduplicated_response_marten";

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
            opts.Discovery.IncludeAssembly(typeof(deduplicated_with_response_on_marten).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = SchemaName;
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a Marten [DeduplicatedWithResponse] test endpoint",
                    type => type != typeof(MartenDeduplicatedResponseEndpoints)));
        }));

        await ((IHost)theHost).ResetResourceState();

        // ResetResourceState leaves documents behind, and the tests count them.
        await theHost.Services.GetRequiredService<IDocumentStore>()
            .Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(DeduplicatedOrderDocument));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_repeat_is_answered_with_the_first_response_and_the_work_is_committed_once()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("marten-" + key);

        var first = await postAsync("/marten-deduplicated-response/orders", request, key, 201);
        var repeat = await postAsync("/marten-deduplicated-response/orders", request, key, 201);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        (await session.Query<DeduplicatedOrderDocument>().CountAsync(x => x.Name == request.Name,
            TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task the_response_is_recorded_after_the_commit()
    {
        await postAsync("/marten-deduplicated-response/orders", new DeduplicatedOrder("warm"),
            Guid.NewGuid().ToString(), 201);

        var source = sourceFor("/marten-deduplicated-response/orders");

        var claim = source.IndexOf(".TryClaimAsync(", StringComparison.Ordinal);
        var commit = source.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        var record = source.IndexOf(".RecordResponseAsync(", StringComparison.Ordinal);
        var write = source.IndexOf("WriteJsonAsync", StringComparison.Ordinal);

        claim.ShouldBeGreaterThan(-1, source);
        claim.ShouldBeLessThan(commit, source);
        commit.ShouldBeLessThan(record, source);
        record.ShouldBeLessThan(write, source);
    }

    [Fact]
    public async Task the_fingerprint_still_reads_a_body_an_audited_member_has_already_read()
    {
        var key = Guid.NewGuid().ToString();

        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "a"), key, 201);
        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "a"), key, 201);
        await postAsync("/marten-deduplicated-response/audited", new AuditedDeduplicatedOrder("audited", "b"), key, 422);

        var source = sourceFor("/marten-deduplicated-response/audited");
        source.IndexOf("EnableBuffering", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("ReadJsonAsync", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task the_fingerprint_still_reads_a_body_the_aggregate_id_was_inferred_from()
    {
        var counterId = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<DeduplicatedCounter>(counterId, new DeduplicatedCounterStarted());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var key = Guid.NewGuid().ToString();
        var command = new IncrementDeduplicatedCounter(counterId, "once");

        var first = await postAsync("/marten-deduplicated-response/counters", command, key, 200);
        var repeat = await postAsync("/marten-deduplicated-response/counters", command, key, 200);
        await postAsync("/marten-deduplicated-response/counters", command with { Note = "twice" }, key, 422);

        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());

        await using var query = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await query.Events.FetchStreamAsync(counterId, token: TestContext.Current.CancellationToken);
        events.Count(x => x.Data is DeduplicatedCounterIncremented).ShouldBe(1);

        var source = sourceFor("/marten-deduplicated-response/counters");
        source.IndexOf("EnableBuffering", StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("ReadJsonAsync", StringComparison.Ordinal), source);
    }

    [Fact]
    public async Task a_tenant_scope_isolates_tenants()
    {
        var key = Guid.NewGuid().ToString();
        var request = new DeduplicatedOrder("per-tenant-" + key);

        var red = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "red");
        var blue = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "blue");
        var redAgain = await postAsync("/marten-deduplicated-response/tenant", request, key, 201, tenant: "red");

        (await blue.ReadAsTextAsync()).ShouldNotBe(await red.ReadAsTextAsync());
        (await redAgain.ReadAsTextAsync()).ShouldBe(await red.ReadAsTextAsync());
    }

    private string sourceFor(string route)
        => theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();

    private Task<IScenarioResult> postAsync<T>(string url, T body, string key, int status, string? tenant = null)
        where T : class
        => theHost.Scenario(x =>
        {
            x.Post.Json(body).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            if (tenant != null) x.WithRequestHeader("tenant", tenant);
            x.StatusCodeShouldBe(status);
        });
}

public class DeduplicatedOrderDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public record AuditedDeduplicatedOrder([property: Audit] string Name, string Detail);

public record DeduplicatedCounterStarted;

public record DeduplicatedCounterIncremented(string Note);

public class DeduplicatedCounter
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(DeduplicatedCounterStarted _)
    {
    }

    public void Apply(DeduplicatedCounterIncremented _) => Count++;
}

// The aggregate id is inferred from the body, which audits it.
public record IncrementDeduplicatedCounter(Guid DeduplicatedCounterId, string Note);

public record DeduplicatedCounterAnswer(int Count);

public static class MartenDeduplicatedResponseEndpoints
{
    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/orders")]
    public static DeduplicatedOrderCreated PostOrder(DeduplicatedOrder request, IDocumentSession session)
    {
        var document = new DeduplicatedOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new DeduplicatedOrderCreated(document.Id);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/audited")]
    public static DeduplicatedOrderCreated PostAudited(AuditedDeduplicatedOrder request, IDocumentSession session)
    {
        var document = new DeduplicatedOrderDocument { Id = Guid.NewGuid(), Name = request.Name };
        session.Store(document);
        return new DeduplicatedOrderCreated(document.Id);
    }

    [DeduplicatedWithResponse(DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/counters")]
    public static DeduplicatedCounterAnswer PostCounter(IncrementDeduplicatedCounter command,
        [WriteAggregate] IEventStream<DeduplicatedCounter> stream)
    {
        stream.AppendOne(new DeduplicatedCounterIncremented(command.Note));
        return new DeduplicatedCounterAnswer(stream.Aggregate!.Count + 1);
    }

    [DeduplicatedWithResponse(DeduplicationScope.Tenant | DeduplicationScope.User)]
    [WolverinePost("/marten-deduplicated-response/tenant")]
    public static DeduplicatedOrderCreated PostTenant(DeduplicatedOrder request) => new(Guid.NewGuid());
}
