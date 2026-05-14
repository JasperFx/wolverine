using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using JasperFx.Events;
using Marten;
using Marten.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// Reproduces the concurrency issue reported with Global Partitioning + Kafka:
/// When multiple message types target the same Marten event stream and are processed
/// via global partitioning with sharded Kafka topics across multiple nodes, concurrent
/// processing of messages for the same stream ID causes EventStreamUnexpectedMaxEventIdException.
///
/// This test simulates the sample app's 2-replica Aspire setup by running 2 Wolverine hosts
/// with a separate publisher host that pumps messages to Kafka input topics.
/// </summary>
public class global_partitioned_aggregate_concurrency : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost _replica1 = null!;
    private IHost _replica2 = null!;
    private IHost _publisher = null!;

    public global_partitioned_aggregate_concurrency(ITestOutputHelper output)
    {
        _output = output;
    }

    private void ConfigureReplica(WolverineOptions opts, string replicaName)
    {
        opts.ServiceName = replicaName;

        opts.Discovery.DisableConventionalDiscovery()
            .IncludeType(typeof(GpStreamCommandAHandler))
            .IncludeType(typeof(GpStreamCommandBHandler))
            .IncludeType(typeof(GpStreamCascadedHandler));

        opts.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "gp_kafka_concurrency";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine();

        var kafka = opts.UseKafka(KafkaContainerFixture.ConnectionString)
            .ConfigureConsumers(c =>
            {
                c.GroupId = "gp-concurrency-test-group";
                // Critical for Kafka co-partitioning: unique ClientId per replica
                c.ClientId = replicaName;
            })
            .AutoProvision();

        opts.Policies.PropagateGroupIdToPartitionKey();
        opts.Policies.AutoApplyTransactions();
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.UseDurableInboxOnAllListeners();
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

        // Global partitioning with sharded Kafka topics matching the sample app
        // 2 shards to match the 2-replica count
        opts.MessagePartitioning.UseInferredMessageGrouping()
            .ByPropertyNamed("Id")
            .GlobalPartitioned(topology =>
            {
                var sharded = topology.UseShardedKafkaTopics("gp-concurrency-test", 3);
                sharded.Message<GpStreamCommandA>();
                sharded.Message<GpStreamCommandB>();
                sharded.Message<GpStreamCascaded>();
            });

        // Listen to external Kafka topics (like sample's topic-one, topic-two)
        opts.ListenToKafkaTopic("gp-concurrency-input-a")
            .DisableConsumerGroupIdStamping()
            .PartitionProcessingByGroupId(PartitionSlots.Five);

        opts.ListenToKafkaTopic("gp-concurrency-input-b")
            .DisableConsumerGroupIdStamping()
            .PartitionProcessingByGroupId(PartitionSlots.Five);

        opts.Services.AddResourceSetupOnStartup();
    }

    public async Task InitializeAsync()
    {
        ConcurrencyTracker.Reset();

        // Start replica 1 first to provision topics and database
        _replica1 = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => ConfigureReplica(opts, "replica-1"))
            .StartAsync();

        // Start replica 2
        _replica2 = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => ConfigureReplica(opts, "replica-2"))
            .StartAsync();

        // Start a publisher host that only publishes to Kafka input topics
        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "publisher";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();

                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision();

                opts.PublishMessage<GpStreamCommandA>()
                    .ToKafkaTopic("gp-concurrency-input-a");

                opts.PublishMessage<GpStreamCommandB>()
                    .ToKafkaTopic("gp-concurrency-input-b");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        // Allow Kafka consumer group rebalancing to settle
        await Task.Delay(5.Seconds());
    }

    public async Task DisposeAsync()
    {
        if (_publisher != null) { await _publisher.StopAsync(); _publisher.Dispose(); }
        if (_replica2 != null) { await _replica2.StopAsync(); _replica2.Dispose(); }
        if (_replica1 != null) { await _replica1.StopAsync(); _replica1.Dispose(); }
    }

    /// <summary>
    /// This test pumps messages from an external publisher to Kafka input topics.
    /// Two replicas each have global partitioning configured with sharded Kafka topics.
    /// Messages for the same stream ID should never be processed concurrently across
    /// any replica, regardless of which input topic they arrive on.
    ///
    /// The bug: with 2 replicas, messages for the same stream arriving on different
    /// input topics can be processed concurrently, causing EventStreamUnexpectedMaxEventIdException.
    /// </summary>
    [Fact]
    public async Task should_not_have_concurrency_exceptions_for_same_stream()
    {
        var store = _replica1.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteAllEventDataAsync();

        var bus = _publisher.Services.GetRequiredService<IMessageBus>();

        // Use a small number of stream IDs but many messages per stream
        var streamIds = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid()).ToArray();
        var messageCount = 0;

        // Pump messages concurrently for the same stream IDs
        var tasks = new List<Task>();
        foreach (var streamId in streamIds)
        {
            for (int i = 0; i < 8; i++)
            {
                var id = streamId;
                var iteration = i;
                tasks.Add(Task.Run(async () =>
                {
                    if (iteration % 2 == 0)
                    {
                        await bus.PublishAsync(new GpStreamCommandA(id, $"name-{iteration}"));
                    }
                    else
                    {
                        await bus.PublishAsync(new GpStreamCommandB(id, $"data-{iteration}"));
                    }

                    Interlocked.Increment(ref messageCount);
                }));
            }
        }

        await Task.WhenAll(tasks);
        _output.WriteLine($"Published {messageCount} messages across {streamIds.Length} streams");

        // Wait for processing to complete across both replicas
        await Task.Delay(45.Seconds());

        var errors = ConcurrencyTracker.Errors.ToList();
        var concurrentAccessCount = ConcurrencyTracker.ConcurrentAccessDetected;

        _output.WriteLine($"Total handled: {ConcurrencyTracker.TotalHandled}");
        _output.WriteLine($"Concurrent access detected: {concurrentAccessCount} times");

        foreach (var error in errors)
        {
            _output.WriteLine($"ERROR: {error}");
        }

        // Verify all messages were processed (32 original + 16 cascaded from CommandA = 48)
        _output.WriteLine($"Expected at least 32 handled messages, got {ConcurrencyTracker.TotalHandled}");

        // The key assertion: no concurrent access to the same stream should occur
        concurrentAccessCount.ShouldBe(0,
            $"Detected {concurrentAccessCount} instances of concurrent access to the same stream. " +
            "Global partitioning should prevent this. Errors:\n" +
            string.Join("\n", errors.Take(10)));
    }
}

// --- Message types with Id property for partitioning ---

public record GpStreamCommandA(Guid Id, string Name);
public record GpStreamCommandB(Guid Id, string Data);
public record GpStreamCascaded(Guid Id, string Source);

// --- Events for the Marten stream ---
public record GpStreamEventA(string Name);
public record GpStreamEventB(string Data);
public record GpStreamEventCascaded(string Source);

// --- Aggregate ---
public class GpStreamAggregate : IRevisioned
{
    public Guid Id { get; set; }
    public long Version { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CascadedCount { get; set; }

    public void Apply(GpStreamEventA _) => ACount++;
    public void Apply(GpStreamEventB _) => BCount++;
    public void Apply(GpStreamEventCascaded _) => CascadedCount++;
}

// --- Concurrency tracking utility ---
public static class ConcurrencyTracker
{
    private static readonly ConcurrentDictionary<string, int> _activeStreams = new();
    private static readonly ConcurrentBag<string> _errors = new();
    private static int _totalHandled;
    private static int _concurrentAccessDetected;

    public static IReadOnlyCollection<string> Errors => _errors;
    public static int TotalHandled => _totalHandled;
    public static int ConcurrentAccessDetected => _concurrentAccessDetected;

    public static void Reset()
    {
        _activeStreams.Clear();
        while (_errors.TryTake(out _)) { }
        _totalHandled = 0;
        _concurrentAccessDetected = 0;
    }

    public static IDisposable TrackStream(string streamId, string handlerName)
    {
        var count = _activeStreams.AddOrUpdate(streamId, 1, (_, existing) => existing + 1);
        if (count > 1)
        {
            Interlocked.Increment(ref _concurrentAccessDetected);
            _errors.Add($"Concurrent access to stream '{streamId}' by {handlerName} (active count: {count})");
        }
        Interlocked.Increment(ref _totalHandled);
        return new StreamTracker(streamId);
    }

    private class StreamTracker : IDisposable
    {
        private readonly string _streamId;
        public StreamTracker(string streamId) => _streamId = streamId;
        public void Dispose() => _activeStreams.AddOrUpdate(_streamId, 0, (_, existing) => existing - 1);
    }
}

// --- Handlers that use [AggregateHandler] to target the same stream ---

[AggregateHandler]
public static class GpStreamCommandAHandler
{
    public static async Task<(Events, OutgoingMessages)> Handle(
        GpStreamCommandA command,
        GpStreamAggregate aggregate)
    {
        using var tracker = ConcurrencyTracker.TrackStream(command.Id.ToString(), nameof(GpStreamCommandAHandler));

        // Simulate some work (like the sample app's random delay)
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 100)));

        var events = new Events { new GpStreamEventA(command.Name) };

        // Cascade a message that also targets the same stream (like the sample app)
        var outgoing = new OutgoingMessages
        {
            new GpStreamCascaded(command.Id, $"from-a-{command.Name}")
        };

        return (events, outgoing);
    }
}

[AggregateHandler]
public static class GpStreamCommandBHandler
{
    public static async Task<Events> Handle(
        GpStreamCommandB command,
        GpStreamAggregate aggregate)
    {
        using var tracker = ConcurrencyTracker.TrackStream(command.Id.ToString(), nameof(GpStreamCommandBHandler));

        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 100)));

        return [new GpStreamEventB(command.Data)];
    }
}

[AggregateHandler]
public static class GpStreamCascadedHandler
{
    public static async Task<Events> Handle(
        GpStreamCascaded command,
        GpStreamAggregate aggregate)
    {
        using var tracker = ConcurrencyTracker.TrackStream(command.Id.ToString(), nameof(GpStreamCascadedHandler));

        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50)));

        return [new GpStreamEventCascaded(command.Source)];
    }
}
