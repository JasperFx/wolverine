using System.Collections.Concurrent;
using Dekaf.Consumer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Kafka.Internals;

namespace Wolverine.Kafka.Tests;

// GH-3147 follow-up: a replayed envelope must look to the pipeline like one the live listener received.
public class kafka_replay_envelope_and_group
{
    [Fact]
    public async Task replayed_envelopes_carry_the_topic_as_their_destination()
    {
        var topic = Guid.NewGuid().ToString();
        var errors = new ErrorLogSink();

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
            opts.PublishAllMessages().ToKafkaTopic(topic).SendInline();
            opts.ListenToKafkaTopic(topic)
                .ProcessInline()
                .ConfigureConsumer(x =>
                {
                    x.GroupId = Guid.NewGuid().ToString();
                    x.AutoOffsetReset = AutoOffsetReset.Earliest;
                });
            opts.Services.AddSingleton<ReplayProbeSink>();
            opts.Services.AddSingleton<ILoggerProvider>(errors);
            opts.Discovery.DisableConventionalDiscovery().IncludeType<ReplayProbeHandler>();
        });

        var sink = host.Services.GetRequiredService<ReplayProbeSink>();
        await host.SendAsync(new ReplayProbeMessage { Id = "live" });
        await waitForAsync(() => sink.Destinations.Count >= 1);

        sink.Destinations.Clear();
        errors.Entries.Clear();

        var result = await host.ReplayKafkaTopicAsync(new KafkaReplayRequest { Topic = topic, FromOffset = 0 },
            token: TestContext.Current.CancellationToken);

        result.RecordsReplayed.ShouldBe(1);
        sink.Destinations.ShouldBe([$"kafka://topic/{topic}"]);

        // Before the fix the executor dereferenced the missing Destination right after the handler returned,
        // so every successfully handled record was logged -- and counted -- as a failure. Asserted on that
        // exception rather than on an empty log: ErrorLogSink is a host-wide ILoggerProvider, so a broker
        // hiccup or a rebalance during this real-Kafka test writes unrelated Error entries.
        errors.Entries.ShouldNotContain(x => x.Contains(nameof(NullReferenceException)));
    }

    [Fact]
    public void the_live_listening_endpoint_of_a_directly_listened_topic_is_the_topic()
    {
        var (_, transport) = buildTransport();
        var topic = transport.Topics["orders"];
        topic.ConsumerConfig = new KafkaConsumerOptions { GroupId = "tenant-a-orders" };

        KafkaReplay.LiveListeningEndpointFor(topic, transport).Uri.ShouldBe(topic.Uri);
    }

    [Fact]
    public void the_live_listening_endpoint_of_a_group_consumed_topic_is_the_group()
    {
        var (options, transport) = buildTransport(transportGroupId: "tenant-a");
        options.ListenToKafkaTopics("payments", "orders");
        var group = transport.TopicGroups.Single();
        var topic = transport.Topics["orders"];

        // Envelope.MarkReceived would stamp the group's Uri, not the topic's, so a replayed envelope has to
        // carry the same one
        KafkaReplay.LiveListeningEndpointFor(topic, transport).Uri.ShouldBe(group.Uri);
        group.Uri.ShouldNotBe(topic.Uri);
    }

    private static (WolverineOptions Options, KafkaTransport Transport) buildTransport(string? transportGroupId = null)
    {
        var options = new WolverineOptions();
        var expression = options.UseKafka("localhost:9092");
        if (transportGroupId != null)
        {
            expression.ConfigureConsumers(x => x.GroupId = transportGroupId);
        }

        return (options, options.Transports.GetOrCreate<KafkaTransport>());
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition not met within {timeoutMs}ms");
    }
}

public class ReplayProbeMessage
{
    public string Id { get; set; } = string.Empty;
}

public class ReplayProbeSink
{
    public ConcurrentQueue<string> Destinations { get; } = new();
}

public class ReplayProbeHandler
{
    public static void Handle(ReplayProbeMessage message, Envelope envelope, ReplayProbeSink sink)
    {
        sink.Destinations.Enqueue(envelope.Destination?.ToString() ?? "(null)");
    }
}

internal sealed class ErrorLogSink : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ErrorLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class ErrorLogger(ErrorLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                sink.Entries.Enqueue($"{category}: {formatter(state, exception)} {exception?.GetType().Name}");
            }
        }
    }
}
