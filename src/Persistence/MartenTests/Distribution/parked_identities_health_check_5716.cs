using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace MartenTests.Distribution;

/// <summary>
/// GH-5716. Marten 10 parks a projection identity whose events fail and keeps the rest of the shard running. When
/// Wolverine runs the shard as one of its agents, the agent's health check says so: Degraded while anything is parked,
/// and Unhealthy once the shard pauses at its parking limit.
/// </summary>
public class parked_identities_health_check_5716 : IAsyncLifetime
{
    private const string SchemaName = "parked_5716";

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await conn.DropSchemaAsync(SchemaName);
    }

    private async Task startHostAsync(Action<StoreOptions>? configure = null)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();

                // One node that runs every agent itself
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;

                        m.Projections.Add<ParkingTallyProjection>(ProjectionLifecycle.Async);

                        configure?.Invoke(m);
                    })
                    .IntegrateWithWolverine(m =>
                    {
                        // Wolverine, not Marten's own daemon, runs the async projection as an agent
                        m.UseWolverineManagedEventSubscriptionDistribution = true;
                    });
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host == null) return;

        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task a_parked_identity_makes_the_projection_agent_degraded()
    {
        await startHostAsync();
        var agent = await findTheProjectionAgentAsync();

        var good = Guid.NewGuid();
        var poison = Guid.NewGuid();
        await appendAsync(session =>
        {
            session.Events.StartStream<ParkingTally>(good, new TallyAdded(2), new TallyAdded(3));
            session.Events.StartStream<ParkingTally>(poison, new TallyAdded(1), new TallyPoisoned());
        });

        // Marten parks the poison stream and carries on with the good one
        await waitUntilAsync(() => Task.FromResult(agent.ParkedCount == 1), "the poison stream to be parked");
        await waitUntilAsync(async () => (await loadAsync(good))?.Total == 5, "the good stream to be projected");

        var result = await agent.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Description.ShouldBe(
            $"Projection {agent.Uri} has 1 parked identity. Replay or discard them with the 'projections parked' command or CritterWatch");
    }

    [Fact]
    public async Task a_shard_paused_at_its_parking_limit_makes_the_projection_agent_unhealthy()
    {
        // This shard may hold one parked identity at most
        await startHostAsync(m => m.Projections.Errors.MaxParkedIdentitiesPerShard = 1);
        var agent = await findTheProjectionAgentAsync();

        await appendAsync(session => session.Events.StartStream<ParkingTally>(Guid.NewGuid(), new TallyPoisoned()));
        await waitUntilAsync(() => Task.FromResult(agent.ParkedCount == 1), "the first poison stream to be parked");

        // Parking a second identity would go past the limit, so the shard pauses instead
        await appendAsync(session => session.Events.StartStream<ParkingTally>(Guid.NewGuid(), new TallyPoisoned()));
        await waitUntilAsync(() => Task.FromResult(agent.Status == AgentStatus.Paused), "the shard to pause");

        agent.Failure!.Category.ShouldBe(ShardFailureCategory.ParkedLimitReached);

        var result = await agent.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Description!.ShouldStartWith(
            $"Projection {agent.Uri} is Paused at its parking limit with 1 identity parked");
    }

    // The live agent Wolverine is running for the ParkingTally projection on this node
    private async Task<EventSubscriptionAgent> findTheProjectionAgentAsync()
    {
        var family = _host.Services.GetRequiredService<IEventSubscriptionAgentFamily>();
        var uri = await family.FindAgentUriAsync("ParkingTally:All", null, TestContext.Current.CancellationToken);
        uri.ShouldNotBeNull();

        var agents = _host.GetRuntime().NodeController!.Agents;
        await waitUntilAsync(() => Task.FromResult(agents.TryGetValue(uri, out var running) && running.Status == AgentStatus.Running),
            "the projection agent to start");

        return agents[uri].ShouldBeOfType<EventSubscriptionAgent>();
    }

    private async Task appendAsync(Action<IDocumentSession> append)
    {
        await using var session = _host.DocumentStore().LightweightSession();
        append(session);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ParkingTally?> loadAsync(Guid id)
    {
        await using var query = _host.DocumentStore().QuerySession();
        return await query.LoadAsync<ParkingTally>(id, TestContext.Current.CancellationToken);
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {what}");
    }
}

public record TallyAdded(int Amount);

// The projection throws on this event, so any stream that has one is a "poison" stream
public record TallyPoisoned;

public class ParkingTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }
}

public partial class ParkingTallyProjection : SingleStreamProjection<ParkingTally, Guid>
{
    public ParkingTallyProjection()
    {
        Name = "ParkingTally";
    }

    public void Apply(TallyAdded e, ParkingTally tally) => tally.Total += e.Amount;

    public void Apply(TallyPoisoned e, ParkingTally tally) =>
        throw new InvalidOperationException("This stream cannot be projected");
}
