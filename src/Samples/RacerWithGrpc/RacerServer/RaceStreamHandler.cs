using RacerContracts;

namespace RacerServer;

/// <summary>
///     Wolverine streaming handler — consumed by <see cref="IMessageBus.StreamAsync{T}"/>
///     from <c>RacingGrpcService.RaceAsync</c>. Per-inbound <see cref="RacerUpdate"/>, the
///     handler updates shared state and yields one <see cref="RacePosition"/> per racer so
///     the client receives a full standings view on every tick.
/// </summary>
public class RaceStreamHandler(RaceState raceState)
{
    public async IAsyncEnumerable<RacePosition> Handle(RacerUpdate update)
    {
        raceState.UpdateSpeed(update.RacerId, update.Speed);

        var standings = ComputeStandings(raceState.GetCurrentSpeeds());
        LogStandings(standings, update.RacerId);

        foreach (var position in standings)
        {
            yield return position;
        }

        // Keeps the method asynchronous — stand-in for an await on I/O (DB, external service).
        await Task.Yield();
    }

    private static List<RacePosition> ComputeStandings(IReadOnlyDictionary<string, double> speeds)
    {
        return speeds
            .OrderByDescending(kv => kv.Value)
            .Select((kv, idx) => new RacePosition
            {
                RacerId = kv.Key,
                Position = idx + 1,
                Speed = kv.Value
            })
            .ToList();
    }

    private static void LogStandings(List<RacePosition> standings, string updatedRacerId)
    {
        var row = string.Join("  |  ", standings.Select(p =>
        {
            var marker = p.RacerId == updatedRacerId ? "*" : " ";
            return $"{marker}#{p.Position} {p.RacerId} {p.Speed,6:F1} km/h";
        }));
        Console.WriteLine(row);
    }
}
