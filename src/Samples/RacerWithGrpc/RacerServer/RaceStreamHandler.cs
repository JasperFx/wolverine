using RacerContracts;

namespace RacerServer;

/// <summary>
///     Wolverine streaming handler — consumed by <see cref="IMessageBus.StreamAsync{T}"/>
///     from <c>RacingGrpcService.RaceAsync</c>. Per-inbound <see cref="RacerUpdate"/>, the
///     handler records the racer's speed and yields one <see cref="RacePosition"/> per racer
///     so the client receives a full standings snapshot on every tick.
/// </summary>
public class RaceStreamHandler(RaceState raceState)
{
    public async IAsyncEnumerable<RacePosition> Handle(RacerUpdate update)
    {
        raceState.UpdateSpeed(update.RacerId, update.Speed);

        var position = 1;
        foreach (var (racerId, speed) in raceState.GetCurrentSpeeds().OrderByDescending(kv => kv.Value))
        {
            yield return new RacePosition { RacerId = racerId, Position = position++, Speed = speed };
        }

        await Task.Yield();
    }
}
