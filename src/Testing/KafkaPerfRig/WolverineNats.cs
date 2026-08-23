using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Nats;
using Wolverine.Nats.Configuration;
using Wolverine.Postgresql;

namespace KafkaPerfRig;

/// <summary>
/// NATS JetStream twins of WolverineRabbit for GH-4026, sharing the corpus, rate loops, stage clock,
/// and recorder. No stamping mapper, so the t2 stage falls back to handler entry; throughput and the
/// handler/total stages are unaffected.
/// </summary>
public static class WolverineNats
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

                opts.UseNats(cfg.NatsUrl)
                    .AutoProvision()
                    .UseJetStream(_ => { })
                    .DefineWorkQueueStream(cfg.NatsStream, _ => { }, $"{cfg.NatsSubjectPrefix}.*");

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToNatsSubject(cfg.NatsSmallSubject)
                    .UseJetStream(cfg.NatsStream, $"rig-small-{cfg.RunId}"), cfg);
                configureListener(opts.ListenToNatsSubject(cfg.NatsLargeSubject)
                    .UseJetStream(cfg.NatsStream, $"rig-large-{cfg.RunId}"), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine nats consumer up: {cfg.Describe()}");

        await host.WaitForShutdownAsync();

        StageRecorder.Dump(cfg.OutDir, "nats-consumer", new
        {
            harness = "wolverine-nats",
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

    private static void configureListener(NatsListenerConfiguration listener, RigConfig cfg)
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

                opts.UseNats(cfg.NatsUrl)
                    .AutoProvision()
                    .UseJetStream(_ => { })
                    .DefineWorkQueueStream(cfg.NatsStream, _ => { }, $"{cfg.NatsSubjectPrefix}.*");

                // Inline sends so every publish is a real JetStream publish; the receive side is what
                // these cells measure
                opts.PublishMessage<SmallEvent>().ToNatsSubject(cfg.NatsSmallSubject).SendInline();
                opts.PublishMessage<LargeEvent>().ToNatsSubject(cfg.NatsLargeSubject).SendInline();
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine nats publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] nats publisher done: {counters.small} small, {counters.large} large. Draining...");

        await Task.Delay(3000);
        await host.StopAsync();
    }
}
