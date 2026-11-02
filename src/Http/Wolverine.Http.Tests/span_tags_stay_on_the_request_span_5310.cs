using System.Collections.Concurrent;
using System.Diagnostics;
using Alba;
using IntegrationTests;
using JasperFx.OpenTelemetry;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5310, the HTTP half. With Marten's <c>TrackConnections</c> on, <c>marten.connection</c> becomes
/// <see cref="Activity.Current"/> as soon as the endpoint's session is built. The handler type and stream tags
/// belong on the request's span, not on the connection span.
/// </summary>
public class span_tags_stay_on_the_request_span_5310 : IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Wolverine" or "Marten" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "span_tags_5310_http";
            opts.OpenTelemetry.TrackConnections = TrackLevel.Verbose;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
            opts.Policies.AutoApplyTransactions();
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints();
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _listener.Dispose();
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task aggregate_endpoint_tags_land_on_the_request_span()
    {
        var id = Guid.NewGuid();
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Events.StartStream<SpanTagged5310HttpAggregate>(id, new SpanTagged5310HttpBumped());
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.Scenario(x =>
        {
            x.Post.Json(new BumpSpanTagged5310Http(id)).ToUrl("/api/span-tags-5310/bump");
            x.StatusCodeShouldBe(204);
        });

        var request = _stopped
            .Where(x => x.Source.Name == "Microsoft.AspNetCore")
            .Where(x => Equals(x.GetTagItem(WolverineTracing.StreamId), id))
            .ShouldHaveSingleItem();

        request.GetTagItem(WolverineTracing.HandlerType).ShouldBe(typeof(SpanTagged5310HttpEndpoint).FullName);
        request.GetTagItem(WolverineTracing.StreamType).ShouldBe(typeof(SpanTagged5310HttpAggregate).FullName);

        var connections = _stopped.Where(x => x.OperationName == "marten.connection").ToArray();
        connections.ShouldNotBeEmpty();
        foreach (var connection in connections)
        {
            connection.GetTagItem(WolverineTracing.HandlerType).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.StreamId).ShouldBeNull();
            connection.GetTagItem(WolverineTracing.StreamType).ShouldBeNull();
        }
    }
}

public class SpanTagged5310HttpAggregate
{
    public Guid Id { get; set; }
    public int Count { get; set; }

    public void Apply(SpanTagged5310HttpBumped _) => Count++;
}

public record SpanTagged5310HttpBumped;

public record BumpSpanTagged5310Http(Guid SpanTagged5310HttpAggregateId);

public static class SpanTagged5310HttpEndpoint
{
    [WolverinePost("/api/span-tags-5310/bump"), EmptyResponse]
    public static SpanTagged5310HttpBumped Post(BumpSpanTagged5310Http command,
        [WriteAggregate] SpanTagged5310HttpAggregate aggregate) => new();
}
