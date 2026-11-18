using System.Diagnostics;
using Alba;
using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Tracking;
using Wolverine.Persistence.EventSourcing;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

// GH-5401: [WaitForProjection<T>] on HTTP endpoints, against Marten with the async daemon running
public class read_your_writes_with_wait_for_projection(ITestOutputHelper output) : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await RywHosts.StartAsync("http_ryw", DaemonMode.Solo);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task the_write_waits_after_the_commit_and_before_the_response()
    {
        // The endpoint is compiled on its first request
        await theHost.Scenario(x =>
        {
            x.Post.Json(new RywAddPoints(Guid.NewGuid(), 1)).ToUrl("/ryw/points");
            x.StatusCodeShouldBe(204);
        });

        var source = sourceFor("/ryw/points");

        var commit = source.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        var wait = source.IndexOf(nameof(ReadYourWrites.WaitForAppendedEventsAsync), StringComparison.Ordinal);
        var headers = source.IndexOf(nameof(ConsistencyTokenHeaders.Write) + "(", StringComparison.Ordinal);

        commit.ShouldBeGreaterThan(0);
        wait.ShouldBeGreaterThan(commit);
        headers.ShouldBeGreaterThan(wait);
    }

    [Fact]
    public async Task the_read_waits_for_the_request_token_before_it_loads_and_not_after()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url($"/ryw/points/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });

        var source = sourceFor("/ryw/points/{id}");

        var wait = source.IndexOf(nameof(ConsistencyTokenHeaders.WaitForRequestTokenAsync), StringComparison.Ordinal);
        wait.ShouldBeGreaterThan(0);
        wait.ShouldBeLessThan(source.IndexOf("RywEndpoints.Get(", StringComparison.Ordinal));
        source.ShouldNotContain(nameof(ReadYourWrites.WaitForAppendedEventsAsync));
    }

    [Fact]
    public async Task the_message_handler_waits_after_the_commit()
    {
        await theHost.MessageBus().InvokeAsync(new RywAddPointsMessage(Guid.NewGuid(), 1),
            TestContext.Current.CancellationToken);

        var source = theHost.GetRuntime().Handlers.ChainFor<RywAddPointsMessage>()!.SourceCode.ShouldNotBeNull();
        output.WriteLine(source);

        var commit = source.IndexOf("SaveChangesAsync", StringComparison.Ordinal);
        commit.ShouldBeGreaterThan(0);
        source.IndexOf(nameof(ReadYourWrites.WaitForAppendedEventsAsync), StringComparison.Ordinal)
            .ShouldBeGreaterThan(commit);
        source.IndexOf(nameof(ReadYourWrites.WaitForEnvelopeTokenAsync), StringComparison.Ordinal)
            .ShouldBeLessThan(source.IndexOf("RywHandlers.Handle(", StringComparison.Ordinal));
    }

    private string sourceFor(string route)
    {
        var source = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == route).SourceCode.ShouldNotBeNull();
        output.WriteLine(source);
        return source;
    }

    [Fact]
    public async Task a_write_waits_for_the_projection_and_returns_the_consistency_token()
    {
        // Several times over: one read straight after a write could be lucky, five in a row are not
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();

            var result = await theHost.Scenario(x =>
            {
                x.Post.Json(new RywAddPoints(id, 3)).ToUrl("/ryw/points");
                x.StatusCodeShouldBe(204);
            });

            var token = result.Context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader].ToString();
            EventStorePosition.TryParse(token, out var position).ShouldBeTrue();
            position!.Sequence.ShouldBeGreaterThan(0);
            result.Context.Response.Headers.ContainsKey(ReadYourWrites.ProjectionTimeoutHeader).ShouldBeFalse();

            await using var session = theHost.DocumentStore().QuerySession();
            var view = await session.LoadAsync<RywPointsView>(id, TestContext.Current.CancellationToken);
            view.ShouldNotBeNull().Total.ShouldBe(3);
        }
    }

    [Fact]
    public async Task a_read_waits_for_the_token_from_an_earlier_write()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var token = await RywHosts.AppendOutsideWolverineAsync(theHost, id, 4);

            var result = await theHost.Scenario(x =>
            {
                x.Get.Url($"/ryw/points/{id}");
                x.WithRequestHeader(ReadYourWrites.ConsistencyTokenHeader, token);
            });

            (await result.ReadAsJsonAsync<RywPointsView>()).ShouldNotBeNull().Total.ShouldBe(4);
        }
    }

    [Fact]
    public async Task a_token_that_is_not_a_position_is_ignored()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url($"/ryw/points/{Guid.NewGuid()}");
            x.WithRequestHeader(ReadYourWrites.ConsistencyTokenHeader, "not a position");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task an_inline_projection_needs_no_wait_but_the_token_is_still_returned()
    {
        var id = Guid.NewGuid();

        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new RywAddPoints(id, 2)).ToUrl("/ryw/inline-points");
            x.StatusCodeShouldBe(204);
        });

        EventStorePosition.TryParse(result.Context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader].ToString(),
            out _).ShouldBeTrue();
        result.Context.Response.Headers.ContainsKey(ReadYourWrites.ProjectionTimeoutHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task a_write_that_appends_nothing_returns_no_token()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new RywAddPoints(Guid.NewGuid(), 0)).ToUrl("/ryw/points");
            x.StatusCodeShouldBe(204);
        });

        result.Context.Response.Headers.ContainsKey(ReadYourWrites.ConsistencyTokenHeader).ShouldBeFalse();
    }

    [Fact]
    public async Task a_message_handler_waits_for_the_projection_before_it_completes()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            await theHost.MessageBus().InvokeAsync(new RywAddPointsMessage(id, 6), TestContext.Current.CancellationToken);

            await using var session = theHost.DocumentStore().QuerySession();
            var view = await session.LoadAsync<RywPointsView>(id, TestContext.Current.CancellationToken);
            view.ShouldNotBeNull().Total.ShouldBe(6);
        }
    }

    [Fact]
    public async Task a_message_handler_waits_for_a_token_on_the_incoming_message()
    {
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var token = await RywHosts.AppendOutsideWolverineAsync(theHost, id, 7);

            var options = new DeliveryOptions();
            options.Headers[ReadYourWrites.ConsistencyTokenHeader] = token;
            var view = await theHost.MessageBus().InvokeAsync<RywPointsView?>(new RywReadPoints(id), options, TestContext.Current.CancellationToken);

            view.ShouldNotBeNull().Total.ShouldBe(7);
        }
    }
}

// GH-5401: no daemon is running, so every wait runs out. The request still succeeds.
public class read_your_writes_when_the_projection_does_not_catch_up : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await RywHosts.StartAsync("http_ryw_stalled", DaemonMode.Disabled);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task the_write_succeeds_after_the_timeout_and_says_so()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new RywAddPoints(Guid.NewGuid(), 3)).ToUrl("/ryw/points");
            x.StatusCodeShouldBe(204);
        });

        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(ProjectionWaitFrames.DefaultTimeoutMs));
        result.Context.Response.Headers[ReadYourWrites.ProjectionTimeoutHeader].ToString().ShouldBe("true");
        EventStorePosition.TryParse(result.Context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader].ToString(),
            out _).ShouldBeTrue();
    }

    [Fact]
    public async Task the_read_succeeds_after_the_timeout_and_says_so()
    {
        var id = Guid.NewGuid();
        var token = await RywHosts.AppendOutsideWolverineAsync(theHost, id, 4);

        var result = await theHost.Scenario(x =>
        {
            x.Get.Url($"/ryw/points/{id}");
            x.WithRequestHeader(ReadYourWrites.ConsistencyTokenHeader, token);
            x.StatusCodeShouldBe(404);
        });

        result.Context.Response.Headers[ReadYourWrites.ProjectionTimeoutHeader].ToString().ShouldBe("true");
    }

    [Fact]
    public async Task a_message_handler_carries_on_after_the_timeout()
    {
        var id = Guid.NewGuid();
        await theHost.MessageBus().InvokeAsync(new RywAddPointsMessage(id, 6), TestContext.Current.CancellationToken);

        await using var session = theHost.DocumentStore().QuerySession();
        (await session.LoadAsync<RywPointsView>(id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}

internal static class RywHosts
{
    public static async Task<IAlbaHost> StartAsync(string schemaName, DaemonMode daemonMode)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(RywHandlers));

            // Wolverine caches the detected application assembly process-wide, so name this one
            opts.Discovery.IncludeAssembly(typeof(RywEndpoints).Assembly);
        });

        builder.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schemaName;
                m.DisableNpgsqlLogging = true;

                m.Projections.Snapshot<RywPointsView>(SnapshotLifecycle.Async);
                m.Projections.Snapshot<RywInlinePointsView>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions()
            .AddAsyncDaemon(daemonMode);

        builder.Services.AddWolverineHttp();

        // No cleaning: every test uses new stream ids, and wiping the events under a running daemon would leave
        // its high water mark ahead of the restarted sequence
        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a read-your-writes endpoint", type => type != typeof(RywEndpoints)))));
    }

    // A write that did not come through Wolverine, as another service or an earlier request would make it
    public static async Task<string> AppendOutsideWolverineAsync(IAlbaHost host, Guid id, int points)
    {
        await using var session = host.DocumentStore().LightweightSession();
        session.Events.StartStream<RywPointsView>(id, new RywPointsAdded(points));
        await session.SaveChangesAsync();

        return ((IStorageOperations)session).LastAppendedPosition.ShouldNotBeNull().ToString();
    }
}

public record RywAddPoints(Guid Id, int Points);

public record RywPointsAdded(int Points);

public class RywPointsView
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public static RywPointsView Create(RywPointsAdded e) => new() { Total = e.Points };
    public void Apply(RywPointsAdded e) => Total += e.Points;
}

public record RywInlinePointsAdded(int Points);

public class RywInlinePointsView
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public static RywInlinePointsView Create(RywInlinePointsAdded e) => new() { Total = e.Points };
    public void Apply(RywInlinePointsAdded e) => Total += e.Points;
}

public static class RywEndpoints
{
    #region sample_wait_for_projection_on_an_http_endpoint

    [WolverinePost("/ryw/points")]
    [WaitForProjection<RywPointsView>]
    public static void Post(RywAddPoints command, IDocumentSession session)
    {
        if (command.Points == 0) return;
        session.Events.Append(command.Id, new RywPointsAdded(command.Points));
    }

    // With a Wolverine-Consistency-Token on the request, this waits for that position before it loads the view
    [WolverineGet("/ryw/points/{id}")]
    [WaitForProjection<RywPointsView>]
    public static Task<RywPointsView?> Get(Guid id, IQuerySession session)
        => session.LoadAsync<RywPointsView>(id);

    #endregion

    [WolverinePost("/ryw/inline-points")]
    [WaitForProjection<RywInlinePointsView>]
    public static void PostInline(RywAddPoints command, IDocumentSession session)
    {
        session.Events.Append(command.Id, new RywInlinePointsAdded(command.Points));
    }
}

public record RywAddPointsMessage(Guid Id, int Points);

public record RywReadPoints(Guid Id);

public static class RywHandlers
{
    #region sample_wait_for_projection_in_a_message_handler

    [WaitForProjection<RywPointsView>]
    public static void Handle(RywAddPointsMessage command, IDocumentSession session)
    {
        session.Events.Append(command.Id, new RywPointsAdded(command.Points));
    }

    #endregion

    [WaitForProjection<RywPointsView>]
    public static Task<RywPointsView?> Handle(RywReadPoints query, IQuerySession session)
        => session.LoadAsync<RywPointsView>(query.Id);
}
