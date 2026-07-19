using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Kafka;

namespace KafkaPerfRig;

public static class WolverinePublisher
{
    public static async Task RunAsync(RigConfig cfg)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(RigHandlers).Assembly;

                opts.UseKafka(cfg.BootstrapServers).AutoProvision();

                configureSubscriber(opts.PublishMessage<SmallEvent>().ToKafkaTopic(cfg.SmallTopic), cfg);
                configureSubscriber(opts.PublishMessage<LargeEvent>().ToKafkaTopic(cfg.LargeTopic), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] publisher done: {counters.small} small, {counters.large} large. Draining...");

        // let the batched sender drain before shutdown
        await Task.Delay(3000);
        await host.StopAsync();
    }

    private static void configureSubscriber(KafkaSubscriberConfiguration subscriber, RigConfig cfg)
    {
        if (cfg.SendMode == "inline")
        {
            subscriber.SendInline();
        }
        else
        {
            subscriber
                .MessageBatchSize(cfg.BatchSize)
                .MessageBatchTimeout(TimeSpan.FromMilliseconds(cfg.BatchTimeoutMs));
        }
    }
}

/// <summary>
/// Shared rate-controlled publish loops used by both the Wolverine and native publishers so the
/// two twins generate identical traffic: N games round-robin, small flow + large flow on
/// PeriodicTimers, warmup flag for the first RIG_WARMUP_S seconds.
/// </summary>
public static class PublishLoops
{
    public static async Task<(int small, int large)> RunAsync(RigConfig cfg,
        Func<string, int, long, bool, Task> publishSmall,
        Func<string, int, long, bool, Task> publishLarge)
    {
        var games = Enumerable.Range(0, cfg.Games).Select(i => $"game-{i:D3}").ToArray();
        var start = Stopwatch.GetTimestamp();
        var totalSeconds = cfg.WarmupSeconds + cfg.DurationSeconds;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(totalSeconds));

        bool inWarmup() => Stopwatch.GetElapsedTime(start).TotalSeconds < cfg.WarmupSeconds;

        async Task<int> loopAsync(double rate, Func<string, int, long, bool, Task> publish)
        {
            if (rate == 0)
            {
                return 0;
            }

            // rate < 0 = max-throughput mode: publish as fast as the publisher can sustain.
            // The consumer-side sustained receive rate is the measurement.
            if (rate < 0)
            {
                var sent = 0;
                while (!cancellation.IsCancellationRequested)
                {
                    var gameId = games[sent % games.Length];
                    try
                    {
                        await publish(gameId, sent, Stopwatch.GetTimestamp(), inWarmup());
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    sent++;
                    if (sent % 50_000 == 0)
                    {
                        Console.WriteLine($"[rig] published {sent}");
                    }
                }

                return sent;
            }

            var count = 0;
            // PeriodicTimer can't reliably tick faster than ~1ms, so high rates publish in
            // bursts on a 10ms cadence instead of one message per tick.
            var interval = TimeSpan.FromSeconds(1.0 / rate);
            var perTick = 1;
            if (interval < TimeSpan.FromMilliseconds(5))
            {
                interval = TimeSpan.FromMilliseconds(10);
                perTick = Math.Max(1, (int)Math.Round(rate / 100.0));
            }

            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellation.Token))
                {
                    for (var i = 0; i < perTick; i++)
                    {
                        var gameId = games[count % games.Length];
                        await publish(gameId, count, Stopwatch.GetTimestamp(), inWarmup());
                        count++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // duration elapsed
            }

            return count;
        }

        var smallTask = loopAsync(cfg.SmallRate, publishSmall);
        var largeTask = loopAsync(cfg.LargeRate, publishLarge);

        return (await smallTask, await largeTask);
    }
}
