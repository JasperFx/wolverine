using System.Collections.Concurrent;
using Dekaf.Consumer;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.Kafka.Tests;

/// <summary>
/// GH-5926. A buffered Kafka listener only commits a record once it finishes, so back pressure must not close its
/// consumer: that would hand the backlog this node is still working through back to the group, and it would be
/// processed twice. Back pressure now pauses the listener's partitions instead. This drives a buffered listener well
/// past its BufferingLimits, then checks that it was latched and resumed on the same listener and consumer, that every
/// message was handled exactly once, and that the group's committed position covers all of them.
/// </summary>
public class buffered_kafka_back_pressure_pauses_in_place_5926
{
    private const int MessageCount = 100;

    private readonly BrokerName brokerName = new("backpressurepause");

    private Task<IHost> publisherAsync(string topicName) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.PublishAllMessages().ToKafkaTopicOnNamedBroker(brokerName, topicName).SendInline();
    });

    private Task<IHost> receiverAsync(string topicName, string groupId) => WolverineHost.ForAsync(opts =>
    {
        opts.AddNamedKafkaBroker(brokerName, KafkaContainerFixture.ConnectionString).AutoProvision();
        opts.ListenToKafkaTopicOnNamedBroker(brokerName, topicName)
            // Latched once more than 20 are queued, resumed at 5 or fewer
            .BufferedInMemory(new BufferingLimits(20, 5))
            .Sequential()
            .ConfigureConsumer(x =>
            {
                x.GroupId = groupId;
                x.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

        opts.Discovery.DisableConventionalDiscovery().IncludeType<BackPressurePauseHandler>();
    });

    [Fact]
    public async Task back_pressure_pauses_and_resumes_the_same_consumer_with_no_duplicates_and_nothing_lost()
    {
        var topic = Guid.NewGuid().ToString();
        var groupId = Guid.NewGuid().ToString();

        var handled = new ConcurrentQueue<string>();
        BackPressurePauseState.Reset(handled);

        using var publisher = await publisherAsync(topic);

        try
        {
            var receiver = await receiverAsync(topic, groupId);
            var agent = kafkaAgent(receiver);

            for (var i = 0; i < MessageCount; i++)
            {
                await publisher.SendAsync(new BackPressurePauseMessage { Id = "m-" + i });
            }

            // The first message is held at the gate, so the rest pile up past the limit and back pressure latches
            await waitForAsync(() => agent.Status == ListeningStatus.TooBusy,
                describe: () => $"status {agent.Status}, queued {agent.QueueCount}");

            var listener = agent.Listener;
            var kafkaListener = listener.ShouldBeOfType<KafkaListener>();

            // Paused in place: the listener is still there and still the same one, its consumer still in the group
            kafkaListener.IsPausedForBackPressure.ShouldBeTrue();

            BackPressurePauseState.Release();

            await waitForAsync(() => handled.Count >= MessageCount && agent.Status == ListeningStatus.Accepting,
                describe: () => $"handled {handled.Count}, status {agent.Status}");

            // Resumed in place rather than rebuilt
            agent.Listener.ShouldBeSameAs(listener);
            kafkaListener.IsPausedForBackPressure.ShouldBeFalse();

            // Every message exactly once
            handled.GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ShouldBeEmpty();
            handled.Distinct().Count().ShouldBe(MessageCount);

            await receiver.StopAsync(TestContext.Current.CancellationToken);
            receiver.Dispose();

            // The group committed past everything the first consumer handled, so a new consumer only sees new work
            var again = new ConcurrentQueue<string>();
            BackPressurePauseState.Reset(again);
            BackPressurePauseState.Release();

            using var second = await receiverAsync(topic, groupId);
            await publisher.SendAsync(new BackPressurePauseMessage { Id = "fresh" });

            await waitForAsync(() => again.Contains("fresh"),
                describe: () => $"second: [{string.Join(", ", again)}]");

            again.ShouldBe(["fresh"]);
        }
        finally
        {
            BackPressurePauseState.Release();
        }
    }

    private static ListeningAgent kafkaAgent(IHost host)
    {
        return host.GetRuntime().Endpoints.ActiveListeners().OfType<ListeningAgent>()
            .Single(x => x.Endpoint is KafkaTopic);
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 60000, Func<string>? describe = null)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"The condition was not met within {timeoutMs}ms. {describe?.Invoke()}");
    }
}

public class BackPressurePauseMessage
{
    public string Id { get; set; } = string.Empty;
}

public static class BackPressurePauseState
{
    private static ConcurrentQueue<string>? _sink;
    private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Reset(ConcurrentQueue<string> sink)
    {
        _gate.TrySetResult();
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sink = sink;
    }

    public static void Release() => _gate.TrySetResult();

    public static Task WaitAsync() => _gate.Task;

    public static void Record(string id) => _sink?.Enqueue(id);
}

public class BackPressurePauseHandler
{
    public static async Task Handle(BackPressurePauseMessage message)
    {
        // Held until the test has seen back pressure latch the listener
        await BackPressurePauseState.WaitAsync();
        BackPressurePauseState.Record(message.Id);
    }
}
