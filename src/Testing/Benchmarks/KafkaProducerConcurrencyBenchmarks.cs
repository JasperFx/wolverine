using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Kafka;
using RunStrategy = BenchmarkDotNet.Engines.RunStrategy;

namespace Benchmarks;

/// <summary>
/// Concurrent publishing across several Kafka topics against the docker-compose broker (localhost:9092), to see
/// whether one producer shared by every topic limits throughput. <see cref="ProducerSharing.PerTopic" /> gives each
/// topic its own producer options, and so its own producer; topics without their own options share one producer
/// per cluster in transports that pool producers. Each operation ends when every message has been handled.
/// Only public Wolverine API is used, so the same file builds against any version of the transport for an A/B.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class KafkaProducerConcurrencyBenchmarks
{
    private const int Topics = 8;
    private const int MessagesPerOperation = 1600;

    private static int _received;
    private static int _expected;
    private static TaskCompletionSource _allReceived = newCompletion();

    private IHost _publisher = null!;
    private IHost _consumer = null!;
    private IDestinationEndpoint[] _endpoints = null!;

    [Params(8, 32)]
    public int Publishers { get; set; }

    [Params(SenderMode.Batched, SenderMode.Inline)]
    public SenderMode Sender { get; set; }

    [Params(ProducerSharing.Shared, ProducerSharing.PerTopic)]
    public ProducerSharing Producers { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var run = Guid.NewGuid().ToString("N");
        var topics = Enumerable.Range(0, Topics).Select(i => $"bdn-concurrency-{run}-{i}").ToArray();

        _consumer = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(KafkaProducerConcurrencyBenchmarks).Assembly;
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka("localhost:9092").AutoProvision();

                foreach (var topic in topics)
                {
                    opts.ListenToKafkaTopic(topic).BeginAtEarliest().BufferedInMemory();
                }
            })
            .StartAsync();

        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(KafkaProducerConcurrencyBenchmarks).Assembly;
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka("localhost:9092").AutoProvision();

                for (var i = 0; i < topics.Length; i++)
                {
                    var publishing = opts.PublishMessage<KafkaProducerConcurrencyMessage>().ToKafkaTopic(topics[i]);
                    if (Sender == SenderMode.Inline)
                    {
                        publishing.SendInline();
                    }
                    else
                    {
                        publishing.BufferedInMemory();
                    }

                    if (Producers == ProducerSharing.PerTopic)
                    {
                        var clientId = $"bdn-concurrency-{i}";
                        publishing.ConfigureProducer(c =>
                        {
                            c.BootstrapServers = "localhost:9092";
                            c.ClientId = clientId;
                        });
                    }
                }
            })
            .StartAsync();

        var bus = _publisher.MessageBus();
        _endpoints = topics.Select(t => bus.EndpointFor(new Uri($"kafka://topic/{t}"))).ToArray();

        // Every consumer has joined its group once one message per topic makes the round trip
        expect(Topics);
        foreach (var endpoint in _endpoints)
        {
            await endpoint.SendAsync(new KafkaProducerConcurrencyMessage(0));
        }

        await _allReceived.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _publisher.StopAsync();
        await _consumer.StopAsync();
        _publisher.Dispose();
        _consumer.Dispose();
    }

    [IterationSetup]
    public void IterationSetup() => expect(MessagesPerOperation);

    [Benchmark(OperationsPerInvoke = MessagesPerOperation)]
    public async Task PublishConcurrentlyAndHandle()
    {
        var perPublisher = MessagesPerOperation / Publishers;
        var publishers = new Task[Publishers];
        for (var p = 0; p < Publishers; p++)
        {
            // Publishers are spread evenly over the topics
            var endpoint = _endpoints[p % Topics];
            publishers[p] = Task.Run(async () =>
            {
                for (var i = 0; i < perPublisher; i++)
                {
                    await endpoint.SendAsync(new KafkaProducerConcurrencyMessage(i));
                }
            });
        }

        await Task.WhenAll(publishers);
        await _allReceived.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }

    internal static void Received()
    {
        if (Interlocked.Increment(ref _received) == Volatile.Read(ref _expected))
        {
            _allReceived.TrySetResult();
        }
    }

    private static void expect(int count)
    {
        _allReceived = newCompletion();
        Volatile.Write(ref _received, 0);
        Volatile.Write(ref _expected, count);
    }

    private static TaskCompletionSource newCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public enum SenderMode
    {
        Batched,
        Inline
    }

    public enum ProducerSharing
    {
        Shared,
        PerTopic
    }
}

public record KafkaProducerConcurrencyMessage(int Number);

public static class KafkaProducerConcurrencyMessageHandler
{
    public static void Handle(KafkaProducerConcurrencyMessage message) => KafkaProducerConcurrencyBenchmarks.Received();
}
