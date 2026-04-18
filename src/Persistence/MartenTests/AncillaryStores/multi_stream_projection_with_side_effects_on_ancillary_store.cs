using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
/// GH-2529: Investigation test for the silent-hang concern when combining
/// (a) a Marten ancillary document store with IntegrateWithWolverine,
/// (b) the async daemon running on that ancillary store,
/// (c) a multi-stream projection that publishes Wolverine messages via
/// RaiseSideEffects().
///
/// If this test passes consistently, the suspected silent failure is not
/// reproducible at the framework integration level. If it hangs or fails,
/// we have a precise reproducer to drive the fix.
/// </summary>
public class multi_stream_projection_with_side_effects_on_ancillary_store : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Primary Marten store + Wolverine integration
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "issue2529_main";
                    m.Events.DatabaseSchemaName = "issue2529_main";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Ancillary store with async daemon + multi-stream projection
                opts.Services.AddMartenStore<IIssue2529Store>(sp =>
                    {
                        var storeOptions = new StoreOptions();
                        storeOptions.Connection(Servers.PostgresConnectionString);
                        storeOptions.DatabaseSchemaName = "issue2529_ancillary";
                        storeOptions.Events.DatabaseSchemaName = "issue2529_ancillary";
                        storeOptions.DisableNpgsqlLogging = true;

                        // The projection that calls RaiseSideEffects → publishes a Wolverine message
                        storeOptions.Projections.Add<Issue2529CounterProjection>(ProjectionLifecycle.Async);
                        return storeOptions;
                    })
                    .IntegrateWithWolverine()
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Issue2529SideEffectHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task AppendAndWaitForProjectionAsync(params Guid[] streamIds)
    {
        using var scope = _host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IIssue2529Store>();

        await using (var session = store.LightweightSession())
        {
            foreach (var id in streamIds)
            {
                session.Events.StartStream<Issue2529Counter>(id, new IncrementCounter());
            }
            await session.SaveChangesAsync();
        }

        // Make sure the async daemon has caught up to all the events we just wrote.
        // This rules out "the daemon is just slow" as the cause of side-effect non-delivery.
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.WaitForNonStaleData(60.Seconds());
    }

    [Fact]
    public async Task projection_side_effect_message_reaches_wolverine_handler()
    {
        var streamId = Guid.NewGuid();

        Issue2529SideEffectHandler.SeenStreamIds.Clear();

        var tracked = await _host
            .TrackActivity()
            .Timeout(60.Seconds())
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<CounterIncremented>(_host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => AppendAndWaitForProjectionAsync(streamId)));

        tracked.Executed.MessagesOf<CounterIncremented>()
            .Where(m => m.StreamId == streamId)
            .ShouldHaveSingleItem();
    }

    // GH-2529: Was failing because concurrent slice processing in Marten's
    // AggregationRunner corrupted MessageContext._outstanding (concurrent
    // List<Envelope>.Add is not thread-safe), dropping most side-effect messages
    // with no exception. Fixed by adding a lock around _outstanding mutations
    // in MessageBus / MessageContext.
    [Fact]
    public async Task multiple_side_effects_in_one_batch_all_reach_wolverine()
    {
        var streamIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        Issue2529SideEffectHandler.SeenStreamIds.Clear();

        var tracked = await _host
            .TrackActivity()
            .Timeout(60.Seconds())
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => AppendAndWaitForProjectionAsync(streamIds)));

        var ourStreamIds = streamIds.ToHashSet();
        var seen = tracked.Executed.MessagesOf<CounterIncremented>()
            .Where(m => ourStreamIds.Contains(m.StreamId))
            .Select(m => m.StreamId)
            .OrderBy(x => x)
            .ToArray();

        seen.ShouldBe(streamIds.OrderBy(x => x).ToArray());
    }
}

// ── Marker interface for the ancillary store ──
public interface IIssue2529Store : IDocumentStore;

// ── Domain ──
public record IncrementCounter;

public class Issue2529Counter
{
    public Guid Id { get; set; }
    public int Count { get; set; }
}

// ── Multi-stream projection that publishes side effects ──
public class Issue2529CounterProjection : MultiStreamProjection<Issue2529Counter, Guid>
{
    public Issue2529CounterProjection()
    {
        Identity<IEvent>(x => x.StreamId);
    }

    public static Issue2529Counter Create(IncrementCounter @event, IEvent metadata) =>
        new() { Id = metadata.StreamId, Count = 1 };

    public void Apply(Issue2529Counter counter, IncrementCounter _) => counter.Count++;

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Issue2529Counter> slice)
    {
        if (slice.Aggregate is not null)
        {
            slice.PublishMessage(new CounterIncremented(slice.Aggregate.Id, slice.Aggregate.Count));
        }
        return ValueTask.CompletedTask;
    }
}

public record CounterIncremented(Guid StreamId, int Count);

// ── Wolverine handler — records what it saw so the test can assert ──
public class Issue2529SideEffectHandler
{
    public static Guid LastSeenStreamId = Guid.Empty;
    public static readonly List<Guid> SeenStreamIds = new();

    public static void Handle(CounterIncremented msg)
    {
        LastSeenStreamId = msg.StreamId;
        lock (SeenStreamIds) SeenStreamIds.Add(msg.StreamId);
        Debug.WriteLine($"Side effect received for stream {msg.StreamId} (count={msg.Count})");
    }
}
