using LoadTesting.Trips;

public static class ConductRepairsHandler
{
    public static async Task<RepairsCompleted> HandleAsync(ConductRepairs message)
    {
        await Task.Delay(Random.Shared.Next(0, 2000));
        return new RepairsCompleted(message.TripId);
    }
}