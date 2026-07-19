using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace KafkaPerfRig;

/// <summary>
/// Raw Confluent.Kafka reproduction of the client's "native" harness: fire-and-forget produce
/// with default librdkafka batching (linger.ms=5), a single sequential consume loop with
/// store-after-process offsets (the same semantics as Wolverine's StoreThenAutoFlush), and the
/// exact same message corpus + stage clock as the Wolverine twin.
/// </summary>
public static class NativeTwin
{
    public static async Task RunPublisherAsync(RigConfig cfg)
    {
        await ensureTopicsAsync(cfg);

        var producerConfig = new ProducerConfig { BootstrapServers = cfg.BootstrapServers };
        using var producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        var errors = 0;
        void handleReport(DeliveryReport<string, byte[]> report)
        {
            if (report.Error.IsError)
            {
                Interlocked.Increment(ref errors);
            }
        }

        Task publish<T>(string topic, T message, string gameId)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            producer.Produce(topic, new Message<string, byte[]> { Key = gameId, Value = bytes }, handleReport);
            return Task.CompletedTask;
        }

        Console.WriteLine($"[rig] native publisher up: {cfg.Describe()}");

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => publish(cfg.SmallTopic,
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small }, gameId),
            (gameId, seq, t0, warmup) => publish(cfg.LargeTopic,
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large }, gameId));

        producer.Flush(TimeSpan.FromSeconds(10));
        Console.WriteLine($"[rig] native publisher done: {counters.small} small, {counters.large} large, {errors} errors");
    }

    public static async Task RunConsumerAsync(RigConfig cfg, CancellationToken token)
    {
        await ensureTopicsAsync(cfg);

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = cfg.BootstrapServers,
            GroupId = $"rig-native-{cfg.RunId}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Subscribe(new[] { cfg.SmallTopic, cfg.LargeTopic });
        Console.WriteLine($"[rig] native consumer up: {cfg.Describe()}");

        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = consumer.Consume(token);
                var t2 = Stopwatch.GetTimestamp();

                string kind;
                string gameId;
                long t0;
                bool warmup;
                if (result.Topic == cfg.LargeTopic)
                {
                    var message = JsonSerializer.Deserialize<LargeEvent>(result.Message.Value)!;
                    (kind, gameId, t0, warmup) = ("large", message.GameId, message.T0, message.Warmup);
                }
                else
                {
                    var message = JsonSerializer.Deserialize<SmallEvent>(result.Message.Value)!;
                    (kind, gameId, t0, warmup) = ("small", message.GameId, message.T0, message.Warmup);
                }

                var t3 = Stopwatch.GetTimestamp();
                if (RigHandlerSettings.HandlerMs > 0)
                {
                    await Task.Delay(RigHandlerSettings.HandlerMs, token);
                }

                StageRecorder.Record(new StageSample(kind, warmup, t0, t2, t3, Stopwatch.GetTimestamp()));
                consumer.StoreOffset(result);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            consumer.Close();
        }

        StageRecorder.Dump(cfg.OutDir, "native-consumer", new
        {
            harness = "native",
            handlerMs = cfg.HandlerMs,
            partitions = cfg.Partitions
        });
    }

    private static async Task ensureTopicsAsync(RigConfig cfg)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = cfg.BootstrapServers })
            .Build();
        foreach (var topic in new[] { cfg.SmallTopic, cfg.LargeTopic })
        {
            try
            {
                await admin.CreateTopicsAsync(new[]
                {
                    new TopicSpecification { Name = topic, NumPartitions = cfg.Partitions }
                });
            }
            catch (CreateTopicsException e) when (e.Results.All(r =>
                         r.Error.Code is ErrorCode.TopicAlreadyExists or ErrorCode.NoError))
            {
                // fine — the other process got there first
            }
        }
    }
}
