using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Runtime.Partitioning;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class Bug_concurrency_with_global_partitioning
{
    private readonly ITestOutputHelper _output;

    public Bug_concurrency_with_global_partitioning(ITestOutputHelper output)
    {
        _output = output;
    }

    private static IHostBuilder BuildSampleServiceHost(string serviceName, ExceptionTracker tracker,
        DestinationTracker destinationTracker)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(tracker))
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Durability.Mode = DurabilityMode.Balanced;

                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SoccerEventTypeOneHandler))
                    .IncludeType(typeof(SoccerEventTypeTwoHandler))
                    .IncludeType(typeof(SoccerInternalEventTypeOneHandler));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "soccer";
                    m.DisableNpgsqlLogging = true;

                    m.Events.StreamIdentity = StreamIdentity.AsString;
                    m.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
                    m.Events.UseIdentityMapForAggregates = true;
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                // Group by the "Id" property on messages
                opts.MessagePartitioning.UseInferredMessageGrouping()
                    .ByPropertyNamed("Id");

                opts.MessagePartitioning.GlobalPartitioned(topology =>
                {
                    topology.UseShardedKafkaTopics("soccer-queue", 3);
                    topology.Message<SoccerEventTypeOne>();
                    topology.Message<SoccerEventTypeTwo>();
                    topology.Message<SoccerInternalEventTypeOne>();
                });

                opts.Policies.PropagateGroupIdToPartitionKey();

                // Match the sample's error handling: retry then discard
                opts.OnException<ConcurrencyException>()
                    .RetryWithCooldown(100.Milliseconds(), 500.Milliseconds(), 2500.Milliseconds())
                    .Then.Discard();

                opts.Services.AddSingleton(Random.Shared);
                opts.Services.AddSingleton(destinationTracker);
            });
    }

    [Fact]
    public async Task should_not_have_concurrency_exceptions_with_global_partitioning()
    {
        var tracker = new ExceptionTracker();
        var destinationTracker = new DestinationTracker();

        // Stand up 3 SampleService hosts to simulate a multi-node cluster
        using var sampleService1 = await BuildSampleServiceHost("SampleService1", tracker, destinationTracker).StartAsync();
        using var sampleService2 = await BuildSampleServiceHost("SampleService2", tracker, destinationTracker).StartAsync();
        using var sampleService3 = await BuildSampleServiceHost("SampleService3", tracker, destinationTracker).StartAsync();

        var hosts = new[] { sampleService1, sampleService2, sampleService3 };

        var cts = new CancellationTokenSource(30.Seconds());

        // Simulate 6 institutions publishing concurrently, spreading across hosts
        var institutionIds = Enumerable.Range(1, 6)
            .Select(i => $"institution-{i:D3}")
            .ToList();

        var tasks = institutionIds.Select((id, index) => Task.Run(async () =>
        {
            var random = new Random();
            // Round-robin across the 3 hosts so different nodes publish for the same aggregate
            var host = hosts[index % hosts.Length];
            var bus = host.Services.GetRequiredService<IMessageBus>();

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var action = random.Next(2);
                    switch (action)
                    {
                        case 0:
                            await bus.PublishAsync(new SoccerEventTypeOne
                            {
                                Id = id,
                                PersonId = Guid.NewGuid().ToString(),
                                Name = "Player-" + random.Next(100),
                                Age = random.Next(18, 40)
                            });
                            break;
                        case 1:
                            await bus.PublishAsync(new SoccerEventTypeTwo
                            {
                                Id = id,
                                PersonId = Guid.NewGuid().ToString(),
                                Occupation = "Position-" + random.Next(11)
                            });
                            break;
                    }

                    await Task.Delay(random.Next(20, 150), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, cts.Token)).ToList();

        await Task.WhenAll(tasks);

        // Give time for in-flight messages to finish processing
        await Task.Delay(10.Seconds());

        // Report destination analysis per aggregate ID
        _output.WriteLine("=== Destination analysis per aggregate ID ===");
        foreach (var kvp in destinationTracker.DestinationsByAggregateId.OrderBy(x => x.Key))
        {
            var destinations = kvp.Value.Distinct().ToList();
            var messageCount = kvp.Value.Count;
            _output.WriteLine($"  {kvp.Key}: {messageCount} messages across {destinations.Count} unique destination(s):");
            foreach (var dest in destinations.OrderBy(x => x.ToString()))
            {
                var count = kvp.Value.Count(d => d == dest);
                _output.WriteLine($"    {dest} ({count} messages)");
            }
        }

        // Check if any aggregate ID was routed to more than one destination
        var multiDestinationAggregates = destinationTracker.DestinationsByAggregateId
            .Where(kvp => kvp.Value.Distinct().Count() > 1)
            .ToList();

        _output.WriteLine($"\nAggregates routed to multiple destinations: {multiDestinationAggregates.Count}");
        foreach (var kvp in multiDestinationAggregates)
        {
            var destinations = kvp.Value.Distinct().ToList();
            _output.WriteLine($"  {kvp.Key} -> {string.Join(", ", destinations)}");
        }

        // Report all captured exceptions
        _output.WriteLine($"\n=== Exceptions ===");
        _output.WriteLine($"Total exceptions recorded: {tracker.Exceptions.Count}");

        var grouped = tracker.Exceptions
            .GroupBy(e => e.GetType().Name)
            .OrderByDescending(g => g.Count());

        foreach (var group in grouped)
        {
            _output.WriteLine($"  {group.Key}: {group.Count()}");
            foreach (var ex in group.Take(3))
            {
                _output.WriteLine($"    {ex.Message}");
            }
        }

        tracker.Exceptions.ShouldBeEmpty(
            "There should be no exceptions when global partitioning is enabled. " +
            $"Got {tracker.Exceptions.Count} exception(s): " +
            string.Join(", ", tracker.Exceptions.GroupBy(e => e.GetType().Name).Select(g => $"{g.Key}={g.Count()}")));
    }
}

/// <summary>
/// Tracks which destination URI each aggregate ID's messages are routed to.
/// Handlers record their envelope destination here so we can verify that
/// global partitioning routes all messages for a given ID to a single endpoint.
/// </summary>
public class DestinationTracker
{
    public ConcurrentDictionary<string, ConcurrentBag<Uri>> DestinationsByAggregateId { get; } = new();

    public void Record(string aggregateId, Uri? destination)
    {
        if (destination == null) return;
        var bag = DestinationsByAggregateId.GetOrAdd(aggregateId, _ => new ConcurrentBag<Uri>());
        bag.Add(destination);
    }
}

/// <summary>
/// Custom logger provider that captures all exceptions logged by Wolverine's
/// handler execution pipeline. Filters out ObjectDisposedException from host
/// shutdown to focus on real concurrency/processing failures.
/// </summary>
public class ExceptionTracker : ILoggerProvider
{
    public ConcurrentBag<Exception> Exceptions { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ExceptionLogger(this);

    public void Dispose() { }

    private class ExceptionLogger(ExceptionTracker tracker) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Error || exception == null) return;

            // Ignore disposal noise from host shutdown
            if (exception is ObjectDisposedException) return;

            tracker.Exceptions.Add(exception);
        }
    }
}

#region Message Types

public class SoccerEventTypeOne
{
    public string Id { get; set; }
    public string PersonId { get; set; }
    public string Name { get; set; }
    public int Age { get; set; }
}

public class SoccerEventTypeTwo
{
    public string Id { get; set; }
    public string PersonId { get; set; }
    public string Occupation { get; set; }
}

public class SoccerInternalEventTypeOne
{
    public string Id { get; set; }
    public string PersonId { get; set; }
    public DateTime Date { get; set; }
}

public class SoccerExternalEventTypeOne
{
    public string Id { get; set; }
}

#endregion

#region Aggregate

public class SoccerAggregate
{
    public string Id { get; set; }
    public Dictionary<string, string> NamesById { get; set; } = [];
    public Dictionary<string, string> OccupationsByName { get; set; } = [];

    public void Apply(SoccerEventTypeOne evt)
    {
        Id = evt.Id;
        NamesById.TryAdd(evt.PersonId, evt.Name);
    }

    public void Apply(SoccerEventTypeTwo evt)
    {
        OccupationsByName.TryAdd(evt.PersonId, evt.Occupation);
    }

    public void Apply(SoccerInternalEventTypeOne evt)
    {
        Id = evt.Id;
        NamesById.TryAdd(evt.PersonId, $"{evt.Date}");
    }
}

#endregion

#region Handlers

[AggregateHandler]
public static class SoccerEventTypeOneHandler
{
    public static async Task<(Events E, OutgoingMessages M)> Handle(
        SoccerEventTypeOne evt,
        SoccerAggregate aggregate,
        Envelope envelope,
        DestinationTracker destinationTracker,
        Random random)
    {
        destinationTracker.Record(evt.Id, envelope.Destination);

        var events = new Events();
        var outgoingMessages = new OutgoingMessages();

        // Simulate work like the original sample
        await Task.Delay(TimeSpan.FromMilliseconds(random.Next(10, 200)));

        events.Add(evt);

        outgoingMessages.Add(new SoccerInternalEventTypeOne
        {
            Id = evt.Id,
            PersonId = evt.PersonId,
            Date = DateTime.UtcNow
        });

        return (events, outgoingMessages);
    }
}

[AggregateHandler]
public static class SoccerEventTypeTwoHandler
{
    public static async Task<Events> Handle(
        SoccerEventTypeTwo evt,
        SoccerAggregate aggregate,
        Envelope envelope,
        DestinationTracker destinationTracker,
        Random random)
    {
        destinationTracker.Record(evt.Id, envelope.Destination);

        var events = new Events();

        await Task.Delay(TimeSpan.FromMilliseconds(random.Next(10, 200)));

        events.Add(evt);

        return events;
    }
}

[AggregateHandler]
public static class SoccerInternalEventTypeOneHandler
{
    public static async Task<(Events E, OutgoingMessages M)> Handle(
        SoccerInternalEventTypeOne evt,
        SoccerAggregate aggregate,
        Envelope envelope,
        DestinationTracker destinationTracker,
        Random random)
    {
        destinationTracker.Record(evt.Id, envelope.Destination);

        var events = new Events();
        var outgoingMessages = new OutgoingMessages();

        await Task.Delay(TimeSpan.FromMilliseconds(random.Next(10, 200)));

        events.Add(evt);

        outgoingMessages.Add(new SoccerExternalEventTypeOne
        {
            Id = evt.Id
        });

        return (events, outgoingMessages);
    }
}

#endregion
