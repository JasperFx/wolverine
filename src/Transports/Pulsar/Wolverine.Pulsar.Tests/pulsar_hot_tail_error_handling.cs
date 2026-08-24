using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// GH-4060. A hot-tail listener reads through a non-durable Pulsar <c>Reader</c> that commits no cursor, so
/// <c>PulsarReaderListener.DeferAsync</c> is necessarily a no-op and nothing routed through it ever comes back.
/// These tests pin down which error handling still functions there and which silently drops the message, because
/// that distinction is the whole substance of the bootstrap warning and the docs.
/// </summary>
[Collection("pulsar")]
public class pulsar_hot_tail_error_handling
{
    // ---- configuration (no broker) ----

    private static PulsarEndpoint endpointFor(string name, bool hotTail) =>
        new($"pulsar://persistent/public/default/{name}".ToUri(), new PulsarTransport())
        {
            IsHotTail = hotTail,
            IsListener = true
        };

    [Fact]
    public void a_hot_tail_endpoint_declares_that_it_cannot_redeliver()
    {
        endpointFor("hot", hotTail: true).supportsRedelivery.ShouldBeFalse();
        endpointFor("ordinary", hotTail: false).supportsRedelivery.ShouldBeTrue();
    }

    [Fact]
    public void requeue_policy_on_a_hot_tail_listener_is_warned_about_at_bootstrap()
    {
        var problem = ListenerConfigurationValidator
            .Validate(endpointFor("hot", hotTail: true), requeuePoliciesConfigured: true)
            .Single();

        problem.Severity.ShouldBe(ListenerConfigurationSeverity.Warning);
        problem.Message.ShouldContain("TailFromLatest()");
        problem.Message.ShouldContain("Requeue()");
    }

    [Fact]
    public void an_ordinary_pulsar_listener_is_not_warned_about()
    {
        ListenerConfigurationValidator
            .Validate(endpointFor("ordinary", hotTail: false), requeuePoliciesConfigured: true)
            .ShouldBeEmpty();
    }

    // ---- end-to-end (Pulsar docker) ----

    private static async Task<IHost> hotTailHostAsync(Action<WolverineOptions> configure)
    {
        var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
                opts.Services.AddSingleton<HotTailFailureSink>();
                opts.Discovery.DisableConventionalDiscovery().IncludeType<HotTailFailureHandler>();
                configure(opts);
            })
            .Build();

        await host.StartAsync();

        // MessageId.Latest: the reader only ever sees what is published after it attaches.
        await Task.Delay(3.Seconds());

        return host;
    }

    private static Task<IHost> publisherAsync(string topic) => Host.CreateDefaultBuilder()
        .UseWolverine(opts =>
        {
            opts.UsePulsar(b => b.ServiceUrl(PulsarContainerFixture.ServiceUrl));
            opts.PublishAllMessages().ToPulsarTopic(topic).SendInline();
            opts.Discovery.DisableConventionalDiscovery();
        })
        .StartAsync();

    /// <summary>
    /// The one form of error handling a hot-tail listener can actually run: a retry that never leaves the
    /// process. Configured on the CHAIN rather than globally, because the Pulsar transport registers a global
    /// AlwaysMatches failure rule at UsePulsar() time that currently pre-empts every global OnException rule --
    /// see the note in the GH-4060 pull request. Chain rules sort ahead of it in CombineRules.
    /// </summary>
    [Fact]
    public async Task in_lane_retries_still_run_on_a_hot_tail_listener()
    {
        var topic = $"persistent://public/default/hottail-retry-{Guid.NewGuid():N}";

        using var publisher = await publisherAsync(topic);
        using var listener = await hotTailHostAsync(opts =>
        {
            opts.ListenToPulsarTopic(topic).ProcessInline().TailFromLatest();
            opts.HandlerGraph.ConfigureHandlerForMessage<HotTailFailingMessage>(chain =>
                chain.OnException<HotTailBoom>().RetryTimes(3));
        });

        var sink = listener.Services.GetRequiredService<HotTailFailureSink>();
        sink.FailuresBeforeSuccess = 2;

        await publisher.MessageBus().SendAsync(new HotTailFailingMessage { Id = "retry-me" });

        await waitForAsync(() => sink.Successes.Count == 1);

        // Three attempts against the same delivery, all inside this process, ending in a success.
        sink.Attempts.ShouldBe(3);
        sink.Successes.ShouldBe(["retry-me"]);
    }

    /// <summary>
    /// The hole itself. Requeue promises another delivery; the Reader has no cursor to produce one from, so the
    /// handler runs exactly once and the message is gone.
    /// </summary>
    [Fact]
    public async Task requeue_on_a_hot_tail_listener_never_redelivers()
    {
        var topic = $"persistent://public/default/hottail-requeue-{Guid.NewGuid():N}";

        using var publisher = await publisherAsync(topic);
        using var listener = await hotTailHostAsync(opts =>
        {
            opts.ListenToPulsarTopic(topic).ProcessInline().TailFromLatest();
            opts.HandlerGraph.ConfigureHandlerForMessage<HotTailFailingMessage>(chain =>
                chain.OnException<HotTailBoom>().Requeue(3));
        });

        var sink = listener.Services.GetRequiredService<HotTailFailureSink>();

        // Would succeed on the second delivery, if there were one.
        sink.FailuresBeforeSuccess = 1;

        await publisher.MessageBus().SendAsync(new HotTailFailingMessage { Id = "requeue-me" });

        await waitForAsync(() => sink.Attempts >= 1);
        await Task.Delay(5.Seconds(), TestContext.Current.CancellationToken);

        sink.Attempts.ShouldBe(1);
        sink.Successes.ShouldBeEmpty();
    }

    /// <summary>
    /// A native dead-letter move is a listener operation and a hot-tail listener is a Reader, so Pulsar's
    /// {topic}-DLQ never sees the failure.
    /// </summary>
    [Fact]
    public async Task native_dead_letter_topic_never_receives_from_a_hot_tail_listener()
    {
        var topic = $"persistent://public/default/hottail-dlq-{Guid.NewGuid():N}";

        await using var probe = await dlqProbeAsync(topic + "-DLQ");

        using var publisher = await publisherAsync(topic);
        using var listener = await hotTailHostAsync(opts =>
            opts.ListenToPulsarTopic(topic)
                .ProcessInline()
                .TailFromLatest()
                .DeadLetterQueueing(DeadLetterTopic.DefaultNative));

        var sink = listener.Services.GetRequiredService<HotTailFailureSink>();
        sink.FailuresBeforeSuccess = int.MaxValue;

        await publisher.MessageBus().SendAsync(new HotTailFailingMessage { Id = "dlq-me" });

        await waitForAsync(() => sink.Attempts >= 1);

        (await probe.CountAsync(5.Seconds())).ShouldBe(0);
    }

    /// <summary>
    /// Positive control for the test above: the same handler, the same failure and the same native dead letter
    /// configuration on an ordinary durable-subscription listener DOES reach the dead letter topic. Without this,
    /// a zero count above would only prove that the probe never worked.
    /// </summary>
    [Fact]
    public async Task native_dead_letter_topic_does_receive_from_an_ordinary_listener()
    {
        var topic = $"persistent://public/default/normal-dlq-{Guid.NewGuid():N}";

        await using var probe = await dlqProbeAsync(topic + "-DLQ");

        using var publisher = await publisherAsync(topic);
        using var listener = await hotTailHostAsync(opts =>
            opts.ListenToPulsarTopic(topic)
                .ProcessInline()
                .WithSharedSubscriptionType()
                .DeadLetterQueueing(DeadLetterTopic.DefaultNative)
                .DisableRetryLetterQueueing());

        var sink = listener.Services.GetRequiredService<HotTailFailureSink>();
        sink.FailuresBeforeSuccess = int.MaxValue;

        await publisher.MessageBus().SendAsync(new HotTailFailingMessage { Id = "dlq-me" });

        (await probe.CountAsync(30.Seconds())).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The one recovery route a hot-tail listener does offer: with a message store configured, the failure lands
    /// in Wolverine's own dead letter table even though nothing native is reachable.
    /// </summary>
    [Fact]
    public async Task the_durable_dead_letter_table_still_catches_a_hot_tail_failure()
    {
        var schema = "hottail_dlq_" + Guid.NewGuid().ToString("N")[..8];
        var topic = $"persistent://public/default/hottail-durabledlq-{Guid.NewGuid():N}";

        using var publisher = await publisherAsync(topic);
        using var listener = await hotTailHostAsync(opts =>
        {
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
            opts.ListenToPulsarTopic(topic).ProcessInline().TailFromLatest();
        });

        var sink = listener.Services.GetRequiredService<HotTailFailureSink>();
        sink.FailuresBeforeSuccess = int.MaxValue;

        await publisher.MessageBus().SendAsync(new HotTailFailingMessage { Id = "durable-dlq" });

        var count = 0L;
        await waitForAsync(() =>
        {
            count = countDeadLetters(schema);
            return count > 0;
        });

        count.ShouldBeGreaterThan(0);
    }

    private static long countDeadLetters(string schema)
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {schema}.wolverine_dead_letters";
        return (long)cmd.ExecuteScalar()!;
    }

    private static async Task<DlqProbe> dlqProbeAsync(string dlqTopic)
    {
        var client = PulsarClient.Builder().ServiceUrl(PulsarContainerFixture.ServiceUrl).Build();
        var consumer = client.NewConsumer()
            .Topic(dlqTopic)
            .SubscriptionName("probe-" + Guid.NewGuid().ToString("N"))
            .SubscriptionType(SubscriptionType.Shared)
            .InitialPosition(SubscriptionInitialPosition.Earliest)
            .Create();

        // Force the subscription to exist before anything is published to the topic.
        await consumer.OnStateChangeTo(ConsumerState.Active, TimeSpan.FromSeconds(30));

        return new DlqProbe(client, consumer);
    }

    private sealed class DlqProbe(IPulsarClient client, IConsumer<ReadOnlySequence<byte>> consumer) : IAsyncDisposable
    {
        public async Task<int> CountAsync(TimeSpan window)
        {
            var count = 0;
            using var cancellation = new CancellationTokenSource(window);

            try
            {
                await foreach (var message in consumer.Messages(cancellation.Token))
                {
                    count++;
                    await consumer.Acknowledge(message, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
            }

            return count;
        }

        public async ValueTask DisposeAsync()
        {
            await consumer.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    private static async Task waitForAsync(Func<bool> condition, int timeoutMs = 30000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition was never met within {timeoutMs}ms");
    }
}

public class HotTailFailingMessage
{
    public string Id { get; set; } = string.Empty;
}

public class HotTailBoom() : Exception("Boom on a hot-tail listener");

public class HotTailFailureSink
{
    private int _attempts;

    public int FailuresBeforeSuccess { get; set; }

    public int Attempts => _attempts;

    public List<string> Successes { get; } = [];

    public void Record(string id)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        if (attempt <= FailuresBeforeSuccess)
        {
            throw new HotTailBoom();
        }

        lock (Successes)
        {
            Successes.Add(id);
        }
    }
}

public class HotTailFailureHandler
{
    public static void Handle(HotTailFailingMessage message, HotTailFailureSink sink)
    {
        sink.Record(message.Id);
    }
}
