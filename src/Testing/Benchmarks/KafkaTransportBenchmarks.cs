using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Kafka;
using RunStrategy = BenchmarkDotNet.Engines.RunStrategy;

namespace Benchmarks;

/// <summary>
/// End-to-end Kafka transport cost against the docker-compose broker (localhost:9092): a publisher host sends a
/// batch of messages to a topic a consumer host listens to, and each operation ends when every message has been
/// handled. The two hosts keep the sending and listening endpoint modes independent, since one endpoint has one
/// mode. Only public Wolverine API is used, so the same file builds against any version of the transport for an A/B.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 3, iterationCount: 10)]
public class KafkaTransportBenchmarks
{
    private const int MessagesPerOperation = 200;

    private static int _received;
    private static int _expected;
    private static TaskCompletionSource _allReceived = newCompletion();

    private IHost _publisher = null!;
    private IHost _consumer = null!;
    private IMessageBus _bus = null!;

    [Params(ListenerMode.Buffered, ListenerMode.Inline)]
    public ListenerMode Listener { get; set; }

    [Params(SenderMode.Batched, SenderMode.Inline)]
    public SenderMode Sender { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var topic = $"bdn-{Listener}-{Sender}-{Guid.NewGuid():N}".ToLowerInvariant();

        _consumer = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(KafkaTransportBenchmarks).Assembly;
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka("localhost:9092").AutoProvision();

                var listening = opts.ListenToKafkaTopic(topic).BeginAtEarliest();
                if (Listener == ListenerMode.Inline)
                {
                    listening.ProcessInline();
                }
                else
                {
                    listening.BufferedInMemory();
                }
            })
            .StartAsync();

        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(KafkaTransportBenchmarks).Assembly;
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseKafka("localhost:9092").AutoProvision();

                var publishing = opts.PublishMessage<KafkaTransportBenchmarkMessage>().ToKafkaTopic(topic);
                if (Sender == SenderMode.Inline)
                {
                    publishing.SendInline();
                }
                else
                {
                    publishing.BufferedInMemory();
                }
            })
            .StartAsync();

        _bus = _publisher.MessageBus();

        // The consumer has joined its group once one message makes the round trip
        expect(1);
        await _bus.PublishAsync(new KafkaTransportBenchmarkMessage(0));
        await _allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60));
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
    public async Task PublishAndHandle()
    {
        for (var i = 0; i < MessagesPerOperation; i++)
        {
            await _bus.PublishAsync(new KafkaTransportBenchmarkMessage(i));
        }

        await _allReceived.Task.WaitAsync(TimeSpan.FromSeconds(60));
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

    public enum ListenerMode
    {
        Buffered,
        Inline
    }

    public enum SenderMode
    {
        Batched,
        Inline
    }
}

public record KafkaTransportBenchmarkMessage(int Number);

public static class KafkaTransportBenchmarkMessageHandler
{
    public static void Handle(KafkaTransportBenchmarkMessage message) => KafkaTransportBenchmarks.Received();
}
