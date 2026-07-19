using System.Collections.Concurrent;
using System.Diagnostics;
using Wolverine;

namespace KafkaPerfRig;

/// <summary>
/// Static knobs the handlers read; set once at consumer startup before the host starts listening.
/// </summary>
public static class RigHandlerSettings
{
    public static int HandlerMs;
    public static bool SequenceByGame;
}

// Deliberately NOT a static class: static classes are abstract under the hood and the handler
// type scanner only keeps concrete types.
public class RigHandlers
{
    // Client-shaped "sequential by key" middleware: a per-game SemaphoreSlim awaited INSIDE the
    // handler window, exactly like the middleware the GH-3490 report describes. The wait lands in
    // the rig's handler_ms stage the same way it lands in wolverine-execution-time for the client.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public static Task Handle(SmallEvent message, Envelope envelope)
    {
        return processAsync("small", message.GameId, message.T0, message.Warmup, envelope);
    }

    public static Task Handle(LargeEvent message, Envelope envelope)
    {
        return processAsync("large", message.GameId, message.T0, message.Warmup, envelope);
    }

    private static async Task processAsync(string kind, string gameId, long t0, bool warmup, Envelope envelope)
    {
        var t3 = Stopwatch.GetTimestamp();

        var t2 = envelope.Headers.TryGetValue(StampingKafkaMapper.ConsumeTimestampHeader, out var raw)
                 && long.TryParse(raw, out var ticks)
            ? ticks
            : t3;

        if (RigHandlerSettings.SequenceByGame)
        {
            var gate = _gates.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                await simulateWorkAsync();
            }
            finally
            {
                gate.Release();
            }
        }
        else
        {
            await simulateWorkAsync();
        }

        StageRecorder.Record(new StageSample(kind, warmup, t0, t2, t3, Stopwatch.GetTimestamp()));
    }

    private static Task simulateWorkAsync()
    {
        return RigHandlerSettings.HandlerMs > 0
            ? Task.Delay(RigHandlerSettings.HandlerMs)
            : Task.CompletedTask;
    }
}
