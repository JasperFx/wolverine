using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Redis;
using Wolverine.Redis.Internal;

namespace KafkaPerfRig;

/// <summary>
/// Redis Streams twin of <see cref="WolverineNats" />, sharing the corpus, rate loops, stage clock and
/// recorder. Added for GH-4329: the durable listener now hands a whole stream read to the receiver in
/// one call instead of dispatching envelope-by-envelope, so a durable endpoint's inbox insert is
/// batched the way RabbitMQ's was in GH-3492 and Kafka/NATS/Pulsar's in GH-4026.
///
/// <para>
/// The cell that matters is <c>RIG_MODE=durable</c>: buffered dispatch was never batched and is
/// unchanged, so it serves as the control. <c>RIG_MAX_RECEIVE=1</c> pins the read batch to one entry,
/// which reproduces the pre-GH-4329 one-at-a-time path inside the same build — the same trick the
/// Kafka topic-group cells use, and far more trustworthy than comparing two builds.
/// </para>
/// </summary>
public static class WolverineRedis
{
    public static async Task RunConsumerAsync(RigConfig cfg)
    {
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        RigHandlerSettings.SequenceByGame = cfg.Sequencing == "semaphore";

        var builder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                if (Environment.GetEnvironmentVariable("RIG_LOG_LISTENER") == "1")
                {
                    logging.AddFilter("Wolverine.Transports.ListeningAgent", LogLevel.Information);
                    logging.AddFilter("Wolverine.Transports.BackPressureAgent", LogLevel.Information);
                }
            })
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(RigHandlers).Assembly;
                opts.Discovery.IncludeType<RigHandlers>();

                opts.UseRedisTransport(cfg.RedisConnection).AutoProvision();

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToRedisStream(cfg.RedisSmallStream, $"rig-small-{cfg.RunId}"), cfg);
                configureListener(opts.ListenToRedisStream(cfg.RedisLargeStream, $"rig-large-{cfg.RunId}"), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine redis consumer up: {cfg.Describe()}");

        await host.WaitForShutdownAsync();

        // Max-throughput cells leave millions of unconsumed entries behind; without this they
        // accumulate across runs and skew every later cell (and eventually exhaust the container's
        // memory). Same hazard the NATS harness deletes its stream for.
        await deleteRunStreamsAsync(cfg);

        StageRecorder.Dump(cfg.OutDir, "redis-consumer", new
        {
            harness = "wolverine-redis",
            mode = cfg.ConsumerMode,
            send = cfg.SendMode,
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            maxReceive = cfg.MaxReceive
        });
    }

    private static async Task deleteRunStreamsAsync(RigConfig cfg)
    {
        try
        {
            await using var mux = await ConnectionMultiplexer.ConnectAsync(cfg.RedisConnection);
            var db = mux.GetDatabase();
            await db.KeyDeleteAsync(cfg.RedisSmallStream);
            await db.KeyDeleteAsync(cfg.RedisLargeStream);
            Console.WriteLine($"[rig] deleted redis streams {cfg.RedisSmallStream}, {cfg.RedisLargeStream}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[rig] could not delete redis streams: {e.Message}");
        }
    }

    private static void configureListener(RedisListenerConfiguration listener, RigConfig cfg)
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

        // Redis names this BatchSize -- it is the XREADGROUP COUNT, and it is exactly what the
        // GH-4329 batched dispatch keys off (`streamResults.Length > 1`). RIG_MAX_RECEIVE=1
        // therefore reproduces the pre-GH-4329 one-at-a-time path inside this same build.
        if (cfg.MaxReceive > 0)
        {
            listener.BatchSize(cfg.MaxReceive);
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

                opts.UseRedisTransport(cfg.RedisConnection).AutoProvision();

                // Inline sends so every publish is a real XADD; the receive side is what these cells measure
                opts.PublishMessage<SmallEvent>().ToRedisStream(cfg.RedisSmallStream).SendInline();
                opts.PublishMessage<LargeEvent>().ToRedisStream(cfg.RedisLargeStream).SendInline();
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine redis publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] redis publisher done: {counters.small} small, {counters.large} large. Draining...");

        await Task.Delay(3000);
        await host.StopAsync();
    }
}
