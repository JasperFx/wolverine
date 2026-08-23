using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Pulsar;

namespace KafkaPerfRig;

/// <summary>
/// Pulsar twins of WolverineRabbit for GH-4026, sharing the corpus, rate loops, stage clock, and
/// recorder. No stamping mapper, so the t2 stage falls back to handler entry; throughput and the
/// handler/total stages are unaffected.
/// </summary>
public static class WolverinePulsar
{
    public static async Task RunConsumerAsync(RigConfig cfg)
    {
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        RigHandlerSettings.SequenceByGame = cfg.Sequencing == "semaphore";

        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(RigHandlers).Assembly;
                opts.Discovery.IncludeType<RigHandlers>();

                opts.UsePulsar(b => b.ServiceUrl(new Uri(cfg.PulsarUrl)));

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToPulsarTopic(cfg.PulsarSmallTopic), cfg);
                configureListener(opts.ListenToPulsarTopic(cfg.PulsarLargeTopic), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine pulsar consumer up: {cfg.Describe()}");

        await host.WaitForShutdownAsync();

        StageRecorder.Dump(cfg.OutDir, "pulsar-consumer", new
        {
            harness = "wolverine-pulsar",
            mode = cfg.ConsumerMode,
            send = cfg.SendMode,
            batchSize = cfg.BatchSize,
            batchTimeoutMs = cfg.BatchTimeoutMs,
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            maxReceive = cfg.MaxReceive
        });
    }

    private static void configureListener(PulsarListenerConfiguration listener, RigConfig cfg)
    {
        switch (cfg.ConsumerMode)
        {
            case "durable":
                listener.UseDurableInbox();
                break;
            case "inline":
                listener.ProcessInline();
                break;
            default:
                listener.BufferedInMemory();
                break;
        }

        if (cfg.MaxParallel > 0)
        {
            listener.MaximumParallelMessages(cfg.MaxParallel);
        }

        if (cfg.MaxReceive > 0)
        {
            listener.MaximumMessagesToReceive(cfg.MaxReceive);
        }
    }

    public static async Task RunPublisherAsync(RigConfig cfg)
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(RigHandlers).Assembly;

                opts.UsePulsar(b => b.ServiceUrl(new Uri(cfg.PulsarUrl)));

                // Inline sends so every publish is a real Pulsar produce; the receive side is what these
                // cells measure
                opts.PublishMessage<SmallEvent>().ToPulsarTopic(cfg.PulsarSmallTopic).SendInline();
                opts.PublishMessage<LargeEvent>().ToPulsarTopic(cfg.PulsarLargeTopic).SendInline();
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine pulsar publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] pulsar publisher done: {counters.small} small, {counters.large} large. Draining...");

        await Task.Delay(3000);
        await host.StopAsync();
    }
}
