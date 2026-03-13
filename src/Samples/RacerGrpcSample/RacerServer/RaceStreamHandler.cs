using RacerContracts;
using Spectre.Console;

namespace RacerServer;

/// <summary>
/// Wolverine streaming handler that processes a stream of racer updates and yields updated standings.
/// Demonstrates IMessageBus.StreamAsync integration with IAsyncEnumerable return types.
/// This handler is invoked by the gRPC endpoint through the Wolverine pipeline.
/// </summary>
public class RaceStreamHandler(RaceState raceState)
{
    /// <summary>
    /// Wolverine streaming handler that processes racer updates and yields race positions.
    /// Returns IAsyncEnumerable to enable streaming through IMessageBus.StreamAsync.
    /// </summary>
    public async IAsyncEnumerable<RacePosition> Handle(RacerUpdate update)
    {
        // Update the race state, a singleton tracking all racer's names, positions, and speeds
        raceState.UpdateSpeed(update.RacerId, update.Speed);
        var standings = ComputeStandings(raceState.GetCurrentSpeeds());
        DisplayLeaderboard(standings, update.RacerId);
        
        // Yield all positions back to the client
        foreach (var position in standings) 
            yield return position;
        
        // Simulate some async work (e.g., database lookup, external API call)
        await Task.Delay(1);
    }

    // We have private methods like this to keep the "business logic" in Handle() concise
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

    private static void DisplayLeaderboard(List<RacePosition> standings, string updatedRacerId)
    {
        var leaderboardParts = standings.Select(pos =>
        {
            var (emoji, posColor) = GetPositionStyle(pos.Position);
            var racerColor = GetRacerColor(pos.Position);
            var speedColor = GetSpeedColor(pos.Position);
            var highlight = pos.RacerId == updatedRacerId ? "bold " : "";

            return $"{emoji} [{highlight}{posColor}]#{pos.Position}[/] [{highlight}{racerColor}]{pos.RacerId,-10}[/] [{highlight}{speedColor}]{pos.Speed,6:F1} mph[/]";
        });

        var leaderboard = string.Join("  [dim]│[/]  ", leaderboardParts);
        AnsiConsole.MarkupLine(leaderboard);
    }

    private static (string Emoji, string Color) GetPositionStyle(int position) => position switch
    {
        1 => ("🥇", "gold1"),
        2 => ("🥈", "silver"),
        3 => ("🥉", "orange3"),
        _ => ("🏁", "dodgerblue1")
    };

    private static string GetRacerColor(int position) => position switch
    {
        1 => "yellow3",
        2 => "darkcyan",
        3 => "magenta3_2",
        _ => "gray80"
    };

    private static string GetSpeedColor(int position) => position switch
    {
        1 => "green",
        2 => "aqua",
        3 => "darkorange3_1",
        _ => "grey"
    };
}
