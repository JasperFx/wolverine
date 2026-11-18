namespace AspireWithPulsar;

public record Ping(Guid Id);

/// <summary>
/// How many pings this service has handled, which GET /pings reports
/// </summary>
public class PingTally
{
    private int _count;

    public int Count => _count;

    public void Increment() => Interlocked.Increment(ref _count);
}

public static class PingHandler
{
    public static void Handle(Ping ping, PingTally tally, ILogger logger)
    {
        tally.Increment();
        logger.LogInformation("Received ping {Id} through Pulsar", ping.Id);
    }
}
