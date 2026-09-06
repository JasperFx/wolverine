using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.AzureServiceBus;
using Wolverine.Postgresql;

namespace KafkaPerfRig;

/// <summary>
/// Azure Service Bus twin of <see cref="WolverineNats" />, sharing the corpus, rate loops, stage clock
/// and recorder. Added for GH-4331, which briefly gave <c>BufferedInMemory</c> and <c>Durable</c>
/// endpoints a computed <c>PrefetchCount</c> default of one receive batch. This lane measured that
/// change as a NULL RESULT against the emulator and it was reverted, so every mode but NativeAck is
/// back at Azure's shipping default of 0. The lane is what will settle GH-4331 for real.
///
/// <para>
/// <b>A/B inside one build</b> via <c>RIG_ASB_PREFETCH</c>: 0 (the default) is now the shipping shape
/// — no client-side buffering on Buffered/Durable — and a positive value reproduces what GH-4331
/// proposed, e.g. <c>RIG_ASB_PREFETCH=20</c> for one receive batch. <c>-1</c> sets an explicit
/// transport-wide 0, which is indistinguishable from the default now that the computed default is
/// gone; it is kept so the ledger's original cells stay reproducible. Preferring an in-build A/B over
/// two worktrees matters more for this transport than most: the emulator's throughput drifts between
/// container restarts.
/// </para>
///
/// <para>
/// <b>The emulator cannot see this knob.</b> It tops out near 35 msg/s with near-zero round-trip
/// latency, and prefetch exists to hide latency. Run this lane against a real namespace
/// (<c>RIG_ASB</c>) before quoting any prefetch number.
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

                // UseAzureServiceBusEmulator when a management string is supplied: AutoProvision
                // talks to the management API, which the emulator exposes on its own port.
                var transport = (cfg.AsbManagementConnection.Length > 0
                        ? opts.UseAzureServiceBusEmulator(cfg.AsbConnection, cfg.AsbManagementConnection)
                        : opts.UseAzureServiceBus(cfg.AsbConnection))
                    .AutoProvision();

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

                _ = (cfg.AsbManagementConnection.Length > 0
                        ? opts.UseAzureServiceBusEmulator(cfg.AsbConnection, cfg.AsbManagementConnection)
                        : opts.UseAzureServiceBus(cfg.AsbConnection))
                    .AutoProvision();

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
