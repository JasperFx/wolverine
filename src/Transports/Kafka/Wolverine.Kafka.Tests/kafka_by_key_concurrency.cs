using System.Collections.Concurrent;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Postgresql;

namespace Wolverine.Kafka.Tests;

// GH-3140: intra-partition concurrency by key. Same-key messages stay ordered (one slot); different
// keys run concurrently. Durable mode (Postgres inbox) is the reliability boundary.
public class kafka_by_key_concurrency : IAsyncLifetime
{
    private IHost _host = null!;
    private string _topic = null!;

    public async Task InitializeAsync()
    {
        ByKeyState.Reset();
        _topic = $"bykey-{Guid.NewGuid():N}";

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka(KafkaContainerFixture.ConnectionString)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka_bykey");

                opts.PublishAllMessages().ToKafkaTopic(_topic).SendInline();
                opts.ListenToKafkaTopic(_topic).ProcessConcurrentlyByKey(PartitionSlots.Five);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<ByKeyHandler>();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task same_key_ordered_different_keys_concurrent()
    {
        var keys = new[] { "A", "B", "C" };

        // Interleave so the consumer sees A0,B0,C0,A1,B1,C1,... — same-key order must be preserved.
        for (var seq = 0; seq < 3; seq++)
        {
            foreach (var key in keys)
            {
                await _host.SendAsync(new ByKeyMessage(key, seq), new DeliveryOptions { PartitionKey = key });
            }
        }

        await waitForTotalAsync(9);

        // Each key handled in publish order on its own slot.
        foreach (var key in keys)
        {
            ByKeyState.Order[key].ShouldBe([0, 1, 2]);
        }

        // Different keys ran concurrently across slots.
        ByKeyState.MaxInFlight.ShouldBeGreaterThanOrEqualTo(2);

        // Exactly-once: every (key, seq) handled once (durable inbox dedup + correct offset handling).
        ByKeyState.Order.Values.Sum(x => x.Count).ShouldBe(9);
    }

    private static async Task waitForTotalAsync(int count, int timeoutMs = 60000)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < cutoff)
        {
            if (ByKeyState.Order.Values.Sum(x => x.Count) >= count) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Only handled {ByKeyState.Order.Values.Sum(x => x.Count)} of {count}");
    }
}

public record ByKeyMessage(string Key, int Seq);

public static class ByKeyState
{
    public static int InFlight;
    public static int MaxInFlight;
    public static readonly ConcurrentDictionary<string, List<int>> Order = new();

    public static void Reset()
    {
        InFlight = 0;
        MaxInFlight = 0;
        Order.Clear();
    }

    public static void RecordMax(int current)
    {
        int prev;
        do
        {
            prev = MaxInFlight;
            if (current <= prev) return;
        } while (Interlocked.CompareExchange(ref MaxInFlight, current, prev) != prev);
    }
}

public class ByKeyHandler
{
    public static async Task Handle(ByKeyMessage message)
    {
        var current = Interlocked.Increment(ref ByKeyState.InFlight);
        ByKeyState.RecordMax(current);

        var list = ByKeyState.Order.GetOrAdd(message.Key, _ => new List<int>());
        lock (list)
        {
            list.Add(message.Seq);
        }

        // Hold the slot long enough that concurrent keys overlap.
        await Task.Delay(250);

        Interlocked.Decrement(ref ByKeyState.InFlight);
    }
}
