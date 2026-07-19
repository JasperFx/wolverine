using System.Diagnostics;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace KafkaPerfRig;

/// <summary>
/// Raw RabbitMQ.Client 7 twin for GH-3492: fire-and-forget publishes (no publisher confirms —
/// the fastest-native anchor, the confirm cliff is its own experiment cell), one
/// AsyncEventingBasicConsumer with manual per-message acks and the same prefetch as a default
/// Wolverine Inline listener. Same corpus, rate loops, and stage clock as every other twin.
/// </summary>
public static class NativeRabbitTwin
{
    public static async Task RunPublisherAsync(RigConfig cfg)
    {
        var factory = new ConnectionFactory { Uri = new Uri(cfg.RabbitUri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await declareQueuesAsync(channel, cfg);

        Console.WriteLine($"[rig] native rabbit publisher up: {cfg.Describe()}");

        async Task publish<T>(string queue, T message)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await channel.BasicPublishAsync("", queue, false, new BasicProperties(), bytes);
        }

        var counters = await PublishLoops.RunAsync(cfg,
            (gameId, seq, t0, warmup) => publish(cfg.SmallQueue,
                new SmallEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Small }),
            (gameId, seq, t0, warmup) => publish(cfg.LargeQueue,
                new LargeEvent { GameId = gameId, Seq = seq, T0 = t0, Warmup = warmup, Payload = Payloads.Large }));

        Console.WriteLine($"[rig] native rabbit publisher done: {counters.small} small, {counters.large} large");
    }

    public static async Task RunConsumerAsync(RigConfig cfg, CancellationToken token)
    {
        var factory = new ConnectionFactory { Uri = new Uri(cfg.RabbitUri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await declareQueuesAsync(channel, cfg);
        await channel.BasicQosAsync(0, cfg.RabbitPrefetch, false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var t2 = Stopwatch.GetTimestamp();

            string kind;
            long t0;
            bool warmup;
            if (ea.RoutingKey == cfg.LargeQueue)
            {
                var message = JsonSerializer.Deserialize<LargeEvent>(ea.Body.Span)!;
                (kind, t0, warmup) = ("large", message.T0, message.Warmup);
            }
            else
            {
                var message = JsonSerializer.Deserialize<SmallEvent>(ea.Body.Span)!;
                (kind, t0, warmup) = ("small", message.T0, message.Warmup);
            }

            var t3 = Stopwatch.GetTimestamp();
            if (RigHandlerSettings.HandlerMs > 0)
            {
                await Task.Delay(RigHandlerSettings.HandlerMs);
            }

            StageRecorder.Record(new StageSample(kind, warmup, t0, t2, t3, Stopwatch.GetTimestamp()));
            await channel.BasicAckAsync(ea.DeliveryTag, false);
        };

        await channel.BasicConsumeAsync(cfg.SmallQueue, false, consumer);
        await channel.BasicConsumeAsync(cfg.LargeQueue, false, consumer);
        Console.WriteLine($"[rig] native rabbit consumer up: {cfg.Describe()}");

        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        StageRecorder.Dump(cfg.OutDir, "native-rabbit-consumer", new
        {
            harness = "native-rabbit",
            handlerMs = cfg.HandlerMs,
            prefetch = cfg.RabbitPrefetch
        });
    }

    private static async Task declareQueuesAsync(IChannel channel, RigConfig cfg)
    {
        foreach (var queue in new[] { cfg.SmallQueue, cfg.LargeQueue })
        {
            await channel.QueueDeclareAsync(queue, true, false, false);
        }
    }
}
