using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Postgresql;

namespace KafkaPerfRig;

/// <summary>
/// GH-4319. The only single-process lane in the rig: no broker at all, just publish into a durable
/// local queue backed by PostgreSQL and handle it in the same host. That isolates precisely what
/// GH-4319 changes — <c>DurableLocalQueue.StoreAndForwardAsync</c>'s inbox <c>INSERT</c>, which used
/// to open its own pooled connection for one row on every single publish.
///
/// <para>
/// <b>A/B inside one build</b> via <c>RIG_STORE_BATCH</c>: 0 (default) leaves the coalesced default,
/// and <c>RIG_STORE_BATCH=1</c> sets <c>StoreIncomingBatchSize</c> to 1, which is exactly the
/// pre-GH-4319 one-INSERT-per-publish path in the same binary. No worktrees, no stash, no chance of
/// measuring fix-versus-fix.
/// </para>
///
/// <para>
/// <b>Two numbers, and the second one is the point.</b> Throughput alone cannot catch the failure
/// mode this design was constrained by: the store gates the send, so any batching window in front of
/// it delays delivery, and GH-3490 measured that shape at a 5,767ms transit p50. So this lane also
/// records the <i>publish call latency</i> — the wall time of the awaited <c>PublishAsync</c>, which
/// on a durable local queue IS the store round trip. It is measured in the publisher, with no
/// cross-thread handoff and therefore no race to misread. Run the trickle cell as well as the
/// saturated one: coalescing that helps at saturation and hurts a lone publish is a regression.
/// </para>
/// </summary>
public static class WolverineLocalQueue
{
    public static async Task RunAsync(RigConfig cfg)
    {
        LocalRigHandlers.HandlerMs = cfg.HandlerMs;

        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(LocalRigHandlers).Assembly;
                opts.Discovery.IncludeType<LocalRigHandlers>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                opts.Policies.UseDurableLocalQueues();

                if (cfg.StoreIncomingBatchSize > 0)
                {
                    opts.Durability.StoreIncomingBatchSize = cfg.StoreIncomingBatchSize;
                }

                if (cfg.MaxParallel > 0)
                {
                    opts.LocalQueueFor<LocalSmallEvent>().MaximumParallelMessages(cfg.MaxParallel);
                    opts.LocalQueueFor<LocalLargeEvent>().MaximumParallelMessages(cfg.MaxParallel);
                }
            });

        using var host = builder.Build();
        await host.StartAsync();

        var effectiveBatch = cfg.StoreIncomingBatchSize > 0 ? cfg.StoreIncomingBatchSize.ToString() : "default(100)";
        Console.WriteLine($"[rig] wolverine local-queue up: {cfg.Describe()} storeBatch={effectiveBatch}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => publishAsync(bus,
                new LocalSmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                warmup),
            (gameId, seq, t0, warmup) => publishAsync(bus,
                new LocalLargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                warmup));

        Console.WriteLine($"[rig] local-queue published: {counters.small} small, {counters.large} large. Draining...");

        // The queue is in-process, so a short drain is enough for the tail to be handled
        await Task.Delay(3000);
        await host.StopAsync();

        PublishLatencyRecorder.Dump(cfg.OutDir, "local-queue");

        StageRecorder.Dump(cfg.OutDir, "local-queue", new
        {
            harness = "wolverine-local-queue",
            mode = "durable-local",
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            storeIncomingBatchSize = effectiveBatch
        });
    }

    /// <summary>
    /// The awaited publish IS the measurement. On a durable local queue PublishAsync does not return
    /// until StoreAndForwardAsync has the row in the inbox, so this wall time is the store round trip
    /// plus nothing else -- and it is where a time-windowed coalescer would show up as added delay.
    /// </summary>
    private static async Task publishAsync(IMessageBus bus, object message, bool warmup)
    {
        var kind = message is LocalLargeEvent ? "large" : "small";
        var start = Stopwatch.GetTimestamp();
        await bus.PublishAsync(message);
        PublishLatencyRecorder.Record(kind, warmup, Stopwatch.GetTimestamp() - start);
    }
}

public class LocalSmallEvent
{
    public string GameId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public long T0 { get; set; }
    public bool Warmup { get; set; }
    public string Payload { get; set; } = string.Empty;
}

public class LocalLargeEvent
{
    public string GameId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public long T0 { get; set; }
    public bool Warmup { get; set; }
    public string Payload { get; set; } = string.Empty;
}

/// <summary>
/// Separate from <see cref="RigHandlers" /> so the local lane's message types never collide with the
/// broker lanes' -- two handlers for one message type is a bootstrap error, not a rig option.
/// </summary>
public class LocalRigHandlers
{
    public static int HandlerMs;

    public static Task Handle(LocalSmallEvent message) => processAsync("small", message.T0, message.Warmup);

    public static Task Handle(LocalLargeEvent message) => processAsync("large", message.T0, message.Warmup);

    private static async Task processAsync(string kind, long t0, bool warmup)
    {
        var t3 = Stopwatch.GetTimestamp();

        if (HandlerMs > 0)
        {
            await Task.Delay(HandlerMs);
        }

        // No transport hop, so there is no separate consume timestamp to stamp: T2 == T3 and dwell is
        // zero by construction. transit_ms is therefore not the interesting column in this lane --
        // publish latency (above) and total_ms are.
        StageRecorder.Record(new StageSample(kind, warmup, t0, t3, t3, Stopwatch.GetTimestamp()));
    }
}

/// <summary>
/// Publisher-side latency capture. Deliberately not routed through <see cref="StageRecorder" />: that
/// one is stamped in the handler, and getting a publish-return timestamp across to it would need a
/// cross-thread handoff that the handler can win, silently reporting a zero transit.
/// </summary>
public static class PublishLatencyRecorder
{
    private static readonly ConcurrentQueue<(string Kind, bool Warmup, long Ticks)> _samples = new();

    public static void Record(string kind, bool warmup, long elapsedTicks)
    {
        _samples.Enqueue((kind, warmup, elapsedTicks));
    }

    public static void Dump(string outDir, string label)
    {
        Directory.CreateDirectory(outDir);
        var samples = _samples.ToArray();

        var summary = new Dictionary<string, object> { ["samples"] = samples.Length };

        foreach (var kind in new[] { "small", "large" })
        {
            // Post-warmup only, same rule StageRecorder uses for its latency percentiles
            var measured = samples.Where(s => s.Kind == kind && !s.Warmup)
                .Select(s => s.Ticks * 1000.0 / Stopwatch.Frequency)
                .OrderBy(x => x)
                .ToArray();

            if (measured.Length == 0) continue;

            double at(double p)
            {
                var index = (int)Math.Ceiling(p / 100.0 * measured.Length) - 1;
                return Math.Round(measured[Math.Clamp(index, 0, measured.Length - 1)], 3);
            }

            summary[kind] = new Dictionary<string, double>
            {
                ["count"] = measured.Length,
                ["p50"] = at(50),
                ["p95"] = at(95),
                ["p99"] = at(99),
                ["max"] = Math.Round(measured[^1], 3),
                ["mean"] = Math.Round(measured.Average(), 3)
            };
        }

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(outDir, $"{label}-publish-latency.json"), json);
        Console.WriteLine($"[rig] {label} publish latency (ms):");
        Console.WriteLine(json);
    }
}
