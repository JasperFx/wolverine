using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace KafkaPerfRig;

/// <summary>
/// RabbitMQ twins of WolverineConsumer/WolverinePublisher for GH-3492, sharing the corpus,
/// rate loops, stage clock, and recorder with the Kafka rig.
/// </summary>
public static class WolverineRabbit
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

                opts.UseRabbitMq(new Uri(cfg.RabbitUri)).AutoProvision();

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToRabbitQueue(cfg.SmallQueue), cfg);
                configureListener(opts.ListenToRabbitQueue(cfg.LargeQueue), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine rabbit consumer up: {cfg.Describe()}");

        await host.WaitForShutdownAsync();

        StageRecorder.Dump(cfg.OutDir, "rabbit-consumer", new
        {
            harness = "wolverine-rabbit",
            mode = cfg.ConsumerMode,
            send = cfg.SendMode,
            batchSize = cfg.BatchSize,
            batchTimeoutMs = cfg.BatchTimeoutMs,
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            listenerCount = cfg.ListenerCount
        });
    }

    private static void configureListener(RabbitMqListenerConfiguration listener, RigConfig cfg)
    {
        listener.UseInterop((runtime, queue) => new StampingRabbitMapper(queue, runtime));

        // "default" leaves Wolverine's out-of-the-box RabbitMQ endpoint mode (Inline) untouched —
        // that IS the R1 experiment.
        switch (cfg.ConsumerMode)
        {
            case "durable":
                listener.UseDurableInbox();
                break;
            case "inline":
                listener.ProcessInline();
                break;
            case "buffered":
                listener.BufferedInMemory();
                break;
        }

        if (cfg.MaxParallel > 0)
        {
            listener.MaximumParallelMessages(cfg.MaxParallel);
        }

        if (cfg.ListenerCount > 0)
        {
            listener.ListenerCount(cfg.ListenerCount);
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

                opts.UseRabbitMq(new Uri(cfg.RabbitUri)).AutoProvision();

                configureSubscriber(opts.PublishMessage<SmallEvent>().ToRabbitQueue(cfg.SmallQueue), cfg);
                configureSubscriber(opts.PublishMessage<LargeEvent>().ToRabbitQueue(cfg.LargeQueue), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine rabbit publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] rabbit publisher done: {counters.small} small, {counters.large} large. Draining...");

        await Task.Delay(3000);
        await host.StopAsync();
    }

    private static void configureSubscriber(RabbitMqSubscriberConfiguration subscriber, RigConfig cfg)
    {
        if (cfg.SendMode == "inline")
        {
            subscriber.SendInline();
        }
        else if (cfg.SendMode == "batched")
        {
            subscriber
                .MessageBatchSize(cfg.BatchSize)
                .MessageBatchTimeout(TimeSpan.FromMilliseconds(cfg.BatchTimeoutMs));
        }
        else if (cfg.SendMode == "buffered")
        {
            // RabbitMQ subscribers default to Inline sending, so the BatchedSender (and the
            // GH-3490 batching-timeout semantics) only apply when a route opts into buffering
            subscriber
                .BufferedInMemory()
                .MessageBatchSize(cfg.BatchSize)
                .MessageBatchTimeout(TimeSpan.FromMilliseconds(cfg.BatchTimeoutMs));
        }
        // "default": leave Wolverine's out-of-the-box sending mode + batching untouched
    }
}
