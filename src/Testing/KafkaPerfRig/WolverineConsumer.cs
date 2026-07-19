using Confluent.Kafka;
using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Kafka;
using Wolverine.Postgresql;

namespace KafkaPerfRig;

public static class WolverineConsumer
{
    public static async Task RunAsync(RigConfig cfg)
    {
        RigHandlerSettings.HandlerMs = cfg.HandlerMs;
        RigHandlerSettings.SequenceByGame = cfg.Sequencing == "semaphore";

        var builder = Host.CreateDefaultBuilder()
            // Keep synchronous console logging out of the measured path; logging cost is its own
            // experiment cell (E11), not ambient noise in every run.
            .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ApplicationAssembly = typeof(RigHandlers).Assembly;
                opts.Discovery.IncludeType<RigHandlers>();

                opts.UseKafka(cfg.BootstrapServers)
                    .AutoProvision()
                    .ConfigureConsumers(c => c.AutoOffsetReset = AutoOffsetReset.Earliest);

                if (cfg.ConsumerMode == "durable")
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, cfg.PostgresSchema);
                }

                configureListener(opts.ListenToKafkaTopic(cfg.SmallTopic), cfg);
                configureListener(opts.ListenToKafkaTopic(cfg.LargeTopic), cfg);
            });

        using var host = builder.Build();
        await host.StartAsync();
        Console.WriteLine($"[rig] wolverine consumer up: {cfg.Describe()}");

        await host.WaitForShutdownAsync();

        StageRecorder.Dump(cfg.OutDir, "wolverine-consumer", new
        {
            harness = "wolverine",
            mode = cfg.ConsumerMode,
            send = cfg.SendMode,
            batchSize = cfg.BatchSize,
            batchTimeoutMs = cfg.BatchTimeoutMs,
            sequencing = cfg.Sequencing,
            handlerMs = cfg.HandlerMs,
            maxParallel = cfg.MaxParallel,
            partitions = cfg.Partitions
        });
    }

    private static void configureListener(KafkaListenerConfiguration listener, RigConfig cfg)
    {
        listener
            .Specification(spec => spec.NumPartitions = (short)cfg.Partitions)
            .UseInterop((_, topic) => new StampingKafkaMapper(topic));

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
    }
}
