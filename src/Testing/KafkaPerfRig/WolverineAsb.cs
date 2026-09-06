using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.Postgresql;

namespace KafkaPerfRig;

/// <summary>
/// Azure Service Bus twin of <see cref="WolverineNats" />, sharing the corpus, rate loops, stage clock
/// and recorder. Added for GH-4331, which gave <c>BufferedInMemory</c> and <c>Durable</c> endpoints a
/// computed <c>PrefetchCount</c> default of one receive batch; before that only NativeAck had one and
/// the other two sat at Azure's shipping default of 0 — no client-side buffering at all, so the batched
/// listener paid a full AMQP round trip per batch.
///
/// <para>
/// <b>A/B inside one build</b> via <c>RIG_ASB_PREFETCH</c>: 0 leaves the endpoint's computed default
/// (the GH-4331 behaviour) and an explicit <c>RIG_ASB_PREFETCH=1</c>... is not the same as the old
/// default. To reproduce the pre-GH-4331 shape set the transport-wide value to 0 explicitly, which
/// still wins over the computed default by design — that is what <c>RIG_ASB_PREFETCH=-1</c> does here.
/// Preferring an in-build A/B over two worktrees matters more for this transport than most: the
/// emulator's throughput drifts between container restarts.
/// </para>
///
/// <para>
/// Requires an Azure Service Bus emulator (docker compose service <c>asb-emulator</c>) or a real
/// namespace via <c>RIG_ASB</c>. The emulator caps out around 50 queues and wedges if its objects are
/// deleted underneath it, so the per-run queue names here are deliberately NOT torn down by the rig —
/// restart the emulator between long sweeps instead.
/// </para>
/// </summary>
public static class WolverineAsb
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

                var transport = opts.UseAzureServiceBus(cfg.AsbConnection).AutoProvision();

                // -1 means "explicit transport-wide 0", i.e. the pre-GH-4331 shape: an explicitly
                // configured 0 still beats the computed per-mode default, by design.
                if (cfg.AsbPrefetch == -1)
                {
                    transport.PrefetchCount(0);
                }
                else if (cfg.AsbPrefetch > 0)
                {
                    transport.PrefetchCount(cfg.AsbPrefetch);
                }

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToAzureServiceBusQueue(cfg.AsbSmallQueue), cfg);
                configureListener(opts.ListenToAzureServiceBusQueue(cfg.AsbLargeQueue), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine asb consumer up: {cfg.Describe()} prefetch={cfg.AsbPrefetch}");

        await host.WaitForShutdownAsync();

        StageRecorder.Dump(cfg.OutDir, "asb-consumer", new
        {
            harness = "wolverine-asb",
            mode = cfg.ConsumerMode,
            send = cfg.SendMode,
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            maxReceive = cfg.MaxReceive,
            prefetch = cfg.AsbPrefetch
        });
    }

    private static void configureListener(AzureServiceBusQueueListenerConfiguration listener, RigConfig cfg)
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

                opts.UseAzureServiceBus(cfg.AsbConnection).AutoProvision();

                // Inline sends so every publish is a real AMQP send; the receive side is what these
                // cells measure
                opts.PublishMessage<SmallEvent>().ToAzureServiceBusQueue(cfg.AsbSmallQueue).SendInline();
                opts.PublishMessage<LargeEvent>().ToAzureServiceBusQueue(cfg.AsbLargeQueue).SendInline();
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine asb publisher up: {cfg.Describe()}");

        var bus = host.MessageBus();

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small },
                new DeliveryOptions { GroupId = gameId }).AsTask(),
            (gameId, seq, t0, warmup) => bus.PublishAsync(
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large },
                new DeliveryOptions { GroupId = gameId }).AsTask());

        Console.WriteLine($"[rig] asb publisher done: {counters.small} small, {counters.large} large. Draining...");

        await Task.Delay(3000);
        await host.StopAsync();
    }
}
