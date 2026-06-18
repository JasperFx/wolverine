using Confluent.Kafka;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;

namespace Wolverine.Kafka.Tests;

// GH-3148: non-blocking tiered retry topics. A transient failure is retried via the tier topics with the
// configured delays and eventually succeeds; a permanent failure walks all tiers and lands in the DLQ.
public class kafka_retry_topics : IAsyncLifetime
{
    private IHost _host = null!;
    private string _topic = null!;

    public async Task InitializeAsync()
    {
        RetryState.Reset();
        _topic = $"retry-{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();
                opts.PublishAllMessages().ToKafkaTopic(_topic).SendInline();
                opts.ListenToKafkaTopic(_topic)
                    .ProcessInline()
                    .EnableNativeDeadLetterQueue()
                    .ConfigureConsumer(x =>
                    {
                        x.GroupId = Guid.NewGuid().ToString();
                        x.AutoOffsetReset = AutoOffsetReset.Earliest;
                    });

                // The tiered, non-blocking retry policy under test.
                opts.OnException<TransientFailure>().MoveToKafkaRetryTopic(1.Seconds(), 2.Seconds());

                opts.Discovery.DisableConventionalDiscovery().IncludeType<FlakyHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task transient_failure_is_retried_via_the_tiers_then_succeeds()
    {
        // Fail the first two attempts (source + first tier), then succeed on the second tier.
        RetryState.FailUntilAttempt = 2;

        await _host.SendAsync(new FlakyMessage { Id = "abc" });

        await waitUntilAsync(() => RetryState.Succeeded, 30_000);

        RetryState.Succeeded.ShouldBeTrue();
        RetryState.Attempts.ShouldBe(3); // source + 1s tier + 2s tier
    }

    [Fact]
    public async Task permanent_failure_walks_all_tiers_then_dead_letters()
    {
        // Never succeed — the message should be tried on the source + both tiers, then DLQ'd.
        RetryState.FailUntilAttempt = 999;

        await _host.SendAsync(new FlakyMessage { Id = "perm" });

        // source + 1s tier + 2s tier = 3 handler attempts, then exhausted.
        await waitUntilAsync(() => RetryState.Attempts >= 3, 30_000);

        // The exhausted message should land in the existing Kafka dead letter queue with retry metadata.
        using var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = KafkaContainerFixture.ConnectionString,
            GroupId = Guid.NewGuid().ToString(),
            AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        consumer.Subscribe("wolverine-dead-letter-queue");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        ConsumeResult<string, byte[]>? dlq = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message != null)
            {
                dlq = result;
                break;
            }
        }
        consumer.Close();

        dlq.ShouldNotBeNull("Exhausted message was not moved to the Kafka dead letter queue");
        // It never succeeded and was tried exactly source + 2 tiers.
        RetryState.Succeeded.ShouldBeFalse();
        RetryState.Attempts.ShouldBe(3);
    }

    private static async Task waitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (condition()) return;
            await Task.Delay(150);
        }

        throw new TimeoutException($"Condition not met within {timeoutMs}ms (attempts so far: {RetryState.Attempts})");
    }
}

public class FlakyMessage
{
    public string Id { get; set; } = string.Empty;
}

public class TransientFailure : Exception
{
    public TransientFailure() : base("transient")
    {
    }
}

public static class RetryState
{
    private static int _attempts;
    public static int FailUntilAttempt;
    public static bool Succeeded;

    public static int Attempts => _attempts;

    public static void Reset()
    {
        _attempts = 0;
        FailUntilAttempt = 0;
        Succeeded = false;
    }

    public static int NextAttempt() => Interlocked.Increment(ref _attempts);
}

public class FlakyHandler
{
    public static void Handle(FlakyMessage message)
    {
        var attempt = RetryState.NextAttempt();
        if (attempt <= RetryState.FailUntilAttempt)
        {
            throw new TransientFailure();
        }

        RetryState.Succeeded = true;
    }
}

public class kafka_retry_naming
{
    [Theory]
    [InlineData(1, "1s")]
    [InlineData(30, "30s")]
    [InlineData(90, "90s")]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    public void compact_delay(int seconds, string expected)
    {
        Wolverine.Kafka.Internals.KafkaRetryNaming.CompactDelay(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);
    }

    [Fact]
    public void retry_topic_name_is_dot_separated()
    {
        Wolverine.Kafka.Internals.KafkaRetryNaming.RetryTopicName("orders", TimeSpan.FromMinutes(5))
            .ShouldBe("orders.retry.5m");
    }
}
