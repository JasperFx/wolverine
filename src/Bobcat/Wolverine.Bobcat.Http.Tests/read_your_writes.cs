using Alba;
using Bobcat;
using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.AspNetCore.Builder;
using Wolverine.Http;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace Wolverine.Bobcat.Http.Tests;

// GH-5401: a spec against an endpoint marked [WaitForProjection<T>] reads the async projection after WhenPosted with
// no wait of its own. The endpoint answers once the projection has the events, with the position in a header.
[BobcatFeature("Adding loyalty points")]
[Collection(nameof(ReadYourWritesCollection))]
public class read_your_writes(ReadYourWritesAppHost app) : WolverineHttpSpec(app.Host)
{
    #region sample_spec_against_an_endpoint_with_wait_for_projection

    [Fact]
    public async Task points_are_in_the_balance_once_the_post_returns()
    {
        var account = Guid.NewGuid();

        await WhenPosted(new AddLoyaltyPoints(account, 25));

        await ThenReadModel<LoyaltyBalance>(account, Specify<LoyaltyBalance>().With(x => x.Points, 25));
    }

    #endregion

    [Fact]
    public async Task the_response_carries_the_consistency_token()
    {
        await WhenPosted(new AddLoyaltyPoints(Guid.NewGuid(), 10));

        var headers = HttpScenario.LastResponse.ShouldNotBeNull().Context.Response.Headers;
        EventStorePosition.TryParse(headers[ReadYourWrites.ConsistencyTokenHeader].ToString(), out _).ShouldBeTrue();
        headers.ContainsKey(ReadYourWrites.ProjectionTimeoutHeader).ShouldBeFalse();
    }
}

public record AddLoyaltyPoints(Guid AccountId, int Points);

public record LoyaltyPointsAdded(int Points);

public class LoyaltyBalance
{
    public Guid Id { get; set; }
    public int Points { get; set; }

    public static LoyaltyBalance Create(LoyaltyPointsAdded e) => new() { Points = e.Points };
    public void Apply(LoyaltyPointsAdded e) => Points += e.Points;
}

public static class LoyaltyEndpoints
{
    [WolverinePost("/api/loyalty/points")]
    [WaitForProjection<LoyaltyBalance>]
    public static void Post(AddLoyaltyPoints command, IDocumentSession session)
        => session.Events.Append(command.AccountId, new LoyaltyPointsAdded(command.Points));
}

// Its own host, so the other specs keep a store with no async projection and no daemon
public sealed class ReadYourWritesAppHost : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = "bobcat_http_ryw";
                m.DisableNpgsqlLogging = true;
                m.Projections.Snapshot<LoyaltyBalance>(SnapshotLifecycle.Async);
            })
            .IntegrateWithWolverine()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Host.UseWolverine(opts =>
        {
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(ReadYourWritesAppHost).Assembly);
        });

        builder.Services.AddWolverineHttp();

        // No cleaning: every spec uses a new account, and wiping the events under a running daemon would leave its
        // high water mark ahead of the restarted sequence
        Host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a read-your-writes endpoint", type => type != typeof(LoyaltyEndpoints)))));
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}

[CollectionDefinition(nameof(ReadYourWritesCollection))]
public class ReadYourWritesCollection : ICollectionFixture<ReadYourWritesAppHost>;
